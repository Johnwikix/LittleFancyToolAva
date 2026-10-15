using System.Diagnostics;
using System.Text.Json;
using FancyToolAva.Services;
using Microsoft.ML.OnnxRuntime;
using SkiaSharp;
#if WINDOWS
using Microsoft.Windows.AI.MachineLearning;
#endif

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private sealed class TimedTile(IVideoSrTileModel model) : IVideoSrTileModel
    {
        internal double InferenceMs;
        internal int Calls;
        public void Infer(float[] input, float[] output, CancellationToken ct)
        {
            long start = Stopwatch.GetTimestamp();
            model.Infer(input, output, ct);
            InferenceMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Calls++;
        }
        public void Dispose() => model.Dispose();
    }

    private static int RunSrPhaseBenchmark(string workDir)
    {
        var records = new List<object>();
        foreach (int window in new[] { 192, 384, 576 })
        {
            var tile = new OrtVideoSrTileModel(Path.GetFullPath("FancyTool/Assets/Models/realesr-general-x4v3_fp16.onnx"), VideoSrBackend.Gpu, window, default);
            var timed = new TimedTile(tile);
            using var session = new VideoFrameUpscaler(timed, tile.Device, window);
            foreach (var size in new[] { (640, 360), (1280, 720) })
            {
                using var input = new SKBitmap(new SKImageInfo(size.Item1, size.Item2, SKColorType.Bgra8888, SKAlphaType.Opaque));
                input.Erase(SKColors.SteelBlue);
                using (session.Upscale(input, 2, default)) { }
                timed.InferenceMs = 0; timed.Calls = 0;
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < 3; i++) using (session.Upscale(input, 2, default)) { }
                double total = timer.Elapsed.TotalMilliseconds / 3;
                var record = new { window, width = size.Item1, height = size.Item2, device = tile.Device.Name,
                    totalMs = total, inferenceAndTransfersMs = timed.InferenceMs / 3, cpuPixelsMs = total - timed.InferenceMs / 3,
                    tilesPerFrame = timed.Calls / 3 };
                records.Add(record);
                Console.WriteLine(JsonSerializer.Serialize(record));
            }
        }
        File.WriteAllText(Path.Combine(workDir, "phases.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static int RunSrDiagnostics(string workDir, bool install)
    {
#if WINDOWS
        var env = OrtEnv.Instance();
        var catalog = ExecutionProviderCatalog.GetDefault();
        if (catalog != null)
        {
            foreach (var provider in catalog.FindAllProviders())
            {
                Console.WriteLine($"CATALOG {provider.Name}");
                if (install && provider.Name.Contains("Tensor", StringComparison.OrdinalIgnoreCase))
                {
                    var ready = provider.EnsureReadyAsync().AsTask().GetAwaiter().GetResult();
                    Console.WriteLine($"INSTALL {provider.Name}: {ready.Status} {ready.DiagnosticText}");
                    if (ready.Status != ExecutionProviderReadyResultState.Success) throw new InvalidOperationException(ready.DiagnosticText);
                    Console.WriteLine($"REGISTER {provider.TryRegister()}");
                }
            }
            catalog.RegisterCertifiedAsync().AsTask().GetAwaiter().GetResult();
        }
        foreach (var ep in env.GetEpDevices()) Console.WriteLine($"DEVICE {ep.EpName} {ep.HardwareDevice.Type}");
#endif
        string path = Path.GetFullPath("FancyTool/Assets/Models/realesr-general-x4v3_fp16.onnx");
        const int window = 384;
        using var input = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Opaque));
        input.Erase(SKColors.SteelBlue);
        var records = new List<object>();
        var timer = Stopwatch.StartNew();
        using (var tile = new OrtVideoSrTileModel(path, VideoSrBackend.Gpu, window, default))
        using (var upscaler = new VideoFrameUpscaler(tile, tile.Device, window))
        {
            Console.WriteLine($"SELECTED {tile.Device}");
            Measure("application", timer.Elapsed.TotalMilliseconds, () => upscaler.Upscale(input, 2, default));
        }
#if WINDOWS
        foreach (var ep in env.GetEpDevices().Where(d => d.EpName.Contains("Tensor", StringComparison.OrdinalIgnoreCase)))
        {
            using var options = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
            foreach (string key in new[] { "height", "width", "in_height", "in_width" }) options.AddFreeDimensionOverrideByName(key, window);
            foreach (string key in new[] { "batch", "batch_size" }) options.AddFreeDimensionOverrideByName(key, 1);
            options.AppendExecutionProvider(env, [ep], new Dictionary<string, string>());
            timer.Restart();
            using var session = new InferenceSession(path, options);
            Console.WriteLine($"EXPLICIT {ep.EpName}: setup={timer.Elapsed.TotalMilliseconds:F1}ms");
        }
#endif
        File.WriteAllText(Path.Combine(workDir, "diagnostics.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        return 0;

        void Measure(string name, double setupMs, Func<SKBitmap> run)
        {
            timer.Restart();
            using (run()) { }
            double coldMs = timer.Elapsed.TotalMilliseconds;
            long allocation = GC.GetTotalAllocatedBytes();
            int collections = GC.CollectionCount(2);
            var times = new double[5];
            for (int i = 0; i < times.Length; i++) { timer.Restart(); using var result = run(); times[i] = timer.Elapsed.TotalMilliseconds; }
            Array.Sort(times);
            var record = new { name, setupMs, coldMs, warmMedianMs = times[2], allocatedPerFrame = (GC.GetTotalAllocatedBytes() - allocation) / 5, gen2Collections = GC.CollectionCount(2) - collections };
            records.Add(record);
            Console.WriteLine(JsonSerializer.Serialize(record));
        }
    }
}
