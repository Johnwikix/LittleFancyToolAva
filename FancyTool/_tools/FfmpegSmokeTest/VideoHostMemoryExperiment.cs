using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FancyToolAva.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static unsafe int RunVideoHostMemory(string workDir)
    {
        WindowsMlAccelerator.CanUseGpuPixels(default);
        string path = VideoPixelGraph.Create("FancyTool/Libraries/VideoSr/fsrcnnx_m_fp16.onnx", "sr16", default);
        using var session = WindowsMlAccelerator.CreateSession(path, VideoSrBackend.VendorGpu, 384, 384, default,
            out _, true, useCudaGraph: false);
        var records = new List<object>();
        try
        {
            var ep = OrtEnv.Instance().GetEpDevices().First(d => WindowsMlAccelerator.IsTensorRtProvider(d.EpName));
            using var memory = ep.GetMemoryInfo(OrtDeviceMemoryType.HOST_ACCESSIBLE);
            using var allocator = new OrtAllocator(session, memory);
            using var hostInput = allocator.Allocate(384 * 384 * 4);
            using var hostOutput = allocator.Allocate(640 * 640 * 4);
            byte[] input = new byte[384 * 384 * 4];
            int[] output = new int[640 * 640];
            new Random(113).NextBytes(input);
            input.CopyTo(new Span<byte>((void*)hostInput.DangerousGetHandle(), input.Length));
            string? expected = null;
            foreach (bool pinned in new[] { false, true, false, true })
            {
                using var i = pinned
                    ? OrtValue.CreateTensorValueWithData(memory, TensorElementType.UInt8, [1, 384, 384, 4], hostInput.DangerousGetHandle(), input.Length)
                    : OrtValue.CreateTensorValueFromMemory(input, [1, 384, 384, 4]);
                using var o = pinned
                    ? OrtValue.CreateTensorValueWithData(memory, TensorElementType.Int32, [1, 640, 640], hostOutput.DangerousGetHandle(), output.Length * 4L)
                    : OrtValue.CreateTensorValueFromMemory(output, [1, 640, 640]);
                using var run = new RunOptions();
                OrtValue[] inputs = [i], outputs = [o];
                string[] inputNames = [session.InputNames.Single()], outputNames = [session.OutputNames.Single()];
                session.Run(run, inputNames, inputs, outputNames, outputs);
                ReadOnlySpan<byte> bytes = pinned ? new((void*)hostOutput.DangerousGetHandle(), output.Length * 4)
                    : MemoryMarshal.AsBytes(output.AsSpan());
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                expected ??= hash;
                if (hash != expected) throw new InvalidOperationException("Pinned output differs");
                var timer = Stopwatch.StartNew();
                for (int repeat = 0; repeat < 1000; repeat++) session.Run(run, inputNames, inputs, outputNames, outputs);
                var record = new { pinned, memory.Name, memory.Id, calls = 1000, elapsedMs = timer.Elapsed.TotalMilliseconds, hash };
                records.Add(record);
                Console.WriteLine(JsonSerializer.Serialize(record));
            }
        }
        catch (Exception error) when (error is OnnxRuntimeException or NotSupportedException)
        {
            records.Add(new { unsupported = true, error = error.ToString() });
            Console.WriteLine(error.Message);
        }
        File.WriteAllText(Path.Combine(workDir, "host-memory.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
