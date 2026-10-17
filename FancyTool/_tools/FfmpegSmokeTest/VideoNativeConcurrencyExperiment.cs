using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime.Tensors;
using FancyToolAva.Services;
using Microsoft.ML.OnnxRuntime;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunVideoNativeConcurrency(string workDir, string[] args)
    {
        WindowsMlAccelerator.CanUseGpuPixels(default);
        if (args.Contains("--native-sr-only")) return RunNativeSrStreams(workDir);
        foreach (var ep in OrtEnv.Instance().GetEpDevices().Where(d => WindowsMlAccelerator.IsTensorRtProvider(d.EpName)))
            Console.WriteLine(JsonSerializer.Serialize(new { ep.EpName, ep.EpOptions.Entries, hardware = ep.HardwareDevice.Metadata.Entries }));
        using var sr = new ConcurrentNativeRunner(false);
        using var rife = new ConcurrentNativeRunner(true);
        string[] References(ConcurrentNativeRunner runner) => Enumerable.Range(0, 4).Select(runner.Run).ToArray();
        var srExpected = References(sr); var rifeExpected = References(rife);
        using var barrier = new Barrier(2);
        int errors = 0;
        void Stress(ConcurrentNativeRunner runner, string[] expected, int count, string label)
        {
            barrier.SignalAndWait();
            for (int i = 0; i < count; i++)
            {
                string hash = runner.Run(i % 4);
                if (hash == expected[i % 4]) continue;
                Interlocked.Increment(ref errors);
                Console.WriteLine($"MISMATCH {label} iteration={i} expected={expected[i % 4]} actual={hash}");
            }
        }
        Task.WaitAll(Task.Run(() => Stress(sr, srExpected, 400, "sr")), Task.Run(() => Stress(rife, rifeExpected, 80, "rife")));
        var modules = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
            .Where(m => m.ModuleName.Contains("onnx", StringComparison.OrdinalIgnoreCase)
                || m.ModuleName.Contains("tensorrt", StringComparison.OrdinalIgnoreCase)
                || m.ModuleName.StartsWith("nvinfer", StringComparison.OrdinalIgnoreCase))
            .Select(m => new { m.ModuleName, m.FileName, m.FileVersionInfo.FileVersion, m.FileVersionInfo.ProductVersion }).ToArray();
        var result = new { errors, srRuns = 400, rifeRuns = 80, modules, scope = "独立原生会话、固定地址、自有可变输入、无 AVFrame/视频队列；直接并发 Run" };
        File.WriteAllText(Path.Combine(workDir, "native-concurrency.json"), JsonSerializer.Serialize(result));
        Console.WriteLine(JsonSerializer.Serialize(result));
        return errors == 0 ? 0 : 1;
    }

    private static int RunNativeSrStreams(string workDir)
    {
        const int degree = 4, count = 400;
        var runners = new List<ConcurrentNativeRunner>();
        try
        {
            for (int i = 0; i < degree; i++) runners.Add(new(false));
            var expected = runners.Select(r => Enumerable.Range(0, 4).Select(r.Run).ToArray()).ToArray();
            using var barrier = new Barrier(degree);
            int errors = 0;
            long nativeBefore = runners.Sum(r => r.NativeTicks);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Task.WaitAll(runners.Select((runner, index) => Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < count; i++)
                    if (runner.Run(i % 4) != expected[index][i % 4]) Interlocked.Increment(ref errors);
            })).ToArray());
            var result = new { errors, degree, calls = degree * count, elapsedMs = timer.Elapsed.TotalMilliseconds,
                nativeMs = (runners.Sum(r => r.NativeTicks) - nativeBefore) * 1000d / System.Diagnostics.Stopwatch.Frequency,
                privateStreams = Environment.GetEnvironmentVariable("FANCYTOOL_TEST_STREAMS") == "1",
                cudaGraph = Environment.GetEnvironmentVariable("FANCYTOOL_TEST_NO_CUDA_GRAPH") != "1" };
            File.WriteAllText(Path.Combine(workDir, "native-sr-streams.json"), JsonSerializer.Serialize(result));
            Console.WriteLine(JsonSerializer.Serialize(result));
            return errors == 0 ? 0 : 1;
        }
        finally { foreach (var runner in runners) runner.Dispose(); }
    }

    private sealed class ConcurrentNativeRunner : IDisposable
    {
        internal long NativeTicks;
        private readonly InferenceSession _session;
        private readonly byte[] _first, _second;
        private readonly int[] _output;
        private readonly float[] _amount = [.5f];
        private readonly OrtValue[] _inputs, _outputs;
        private readonly string[] _inputNames, _outputNames;
        private readonly NativeCudaStreamExperiment? _stream;
        private readonly bool _deviceBuffers;
        private readonly List<nint> _inputBuffers = [];
        private nint _outputBuffer;
        internal ConcurrentNativeRunner(bool rife)
        {
            int width = rife ? 1920 : 384, height = rife ? 1080 : 384;
            string source = rife ? Path.Combine(AppPaths.ModelsDirectory, "Interpolation/rife_v4.25_lite_v2.onnx")
                : "FancyTool/Libraries/VideoSr/fsrcnnx_m_fp16.onnx";
            string path = VideoPixelGraph.Create(source, rife ? "rife" : "sr16", default);
            if (Environment.GetEnvironmentVariable("FANCYTOOL_TEST_STREAMS") == "1") _stream = new();
            _session = _stream?.CreateSession(path, width, height)
                ?? WindowsMlAccelerator.CreateSession(path, VideoSrBackend.VendorGpu, width, height, default, out _, true,
                    useCudaGraph: Environment.GetEnvironmentVariable("FANCYTOOL_TEST_NO_CUDA_GRAPH") != "1");
            _deviceBuffers = _stream != null && Environment.GetEnvironmentVariable("FANCYTOOL_TEST_DEVICE_BUFFERS") == "1";
            _first = new byte[width * height * 4]; _second = new byte[_first.Length];
            _output = new int[rife ? width * height : (width - 64) * (height - 64) * 4];
            long[] shape = [1, height, width, 4];
            OrtValue Input(byte[] array, long[] dimensions)
            {
                if (!_deviceBuffers) return OrtValue.CreateTensorValueFromMemory(array, dimensions);
                var pointer = _stream!.Allocate(array.Length);
                _inputBuffers.Add(pointer);
                using var memory = OrtEnv.Instance().GetEpDevices().First(d => WindowsMlAccelerator.IsTensorRtProvider(d.EpName))
                    .GetMemoryInfo(OrtDeviceMemoryType.DEFAULT);
                return OrtValue.CreateTensorValueWithData(memory, TensorElementType.UInt8, dimensions, pointer, array.Length);
            }
            OrtValue Time()
            {
                if (!_deviceBuffers) return OrtValue.CreateTensorValueFromMemory(_amount, [1]);
                var pointer = _stream!.Allocate(4); _inputBuffers.Add(pointer);
                using var memory = OrtEnv.Instance().GetEpDevices().First(d => WindowsMlAccelerator.IsTensorRtProvider(d.EpName)).GetMemoryInfo(OrtDeviceMemoryType.DEFAULT);
                return OrtValue.CreateTensorValueWithData(memory, TensorElementType.Float, [1], pointer, 4);
            }
            _inputs = rife ? [Input(_first, shape), Input(_second, shape), Time()]
                : [Input(_first, shape)];
            long[] outputShape = rife ? [1, height, width] : [1, (height - 64) * 2, (width - 64) * 2];
            if (_deviceBuffers)
            {
                _outputBuffer = _stream!.Allocate(_output.Length * sizeof(int));
                using var memory = OrtEnv.Instance().GetEpDevices().First(d => WindowsMlAccelerator.IsTensorRtProvider(d.EpName)).GetMemoryInfo(OrtDeviceMemoryType.DEFAULT);
                _outputs = [OrtValue.CreateTensorValueWithData(memory, TensorElementType.Int32, outputShape, _outputBuffer, _output.Length * sizeof(int))];
            }
            else _outputs = [OrtValue.CreateTensorValueFromMemory(_output, outputShape)];
            _inputNames = rife ? [VideoPixelGraph.Prefix + "first", VideoPixelGraph.Prefix + "second", VideoPixelGraph.Prefix + "amount"] : [VideoPixelGraph.Prefix + "first"];
            _outputNames = [VideoPixelGraph.Prefix + "output"];
        }
        internal string Run(int fixture)
        {
            Array.Fill(_first, (byte)(20 + fixture * 31)); Array.Fill(_second, (byte)(235 - fixture * 17));
            _amount[0] = (fixture + 1) / 6f;
            using var options = new RunOptions();
            using var scope = _stream?.Enter();
            if (_deviceBuffers)
            {
                _stream!.Upload(_inputBuffers[0], _first);
                if (_inputBuffers.Count > 1)
                {
                    _stream.Upload(_inputBuffers[1], _second);
                    _stream.Upload(_inputBuffers[2], MemoryMarshal.AsBytes(_amount.AsSpan()));
                }
            }
            long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
            _session.Run(options, _inputNames, _inputs, _outputNames, _outputs);
            _stream?.Synchronize();
            NativeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - stamp;
            if (_deviceBuffers) _stream!.Download(_outputBuffer, MemoryMarshal.AsBytes(_output.AsSpan()));
            return Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(_output.AsSpan())));
        }
        public void Dispose()
        {
            using (_stream?.Enter())
            {
                _stream?.Synchronize();
                foreach (var value in _inputs) value.Dispose();
                foreach (var value in _outputs) value.Dispose();
                _session.Dispose();
            }
            _stream?.Dispose();
        }
    }
}
