using System.Diagnostics;
using System.Text.Json;
using FancyToolAva.Services;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunNanoVsrBenchmark(string workDir)
    {
        string path = Path.Combine(AppContext.BaseDirectory, ModelManifest.NanoVsr.BundledRelativePath!);
        var records = new List<object>();
        foreach (var setting in new[] { (320, 180, 4), (640, 360, 2) })
        {
            var timer = Stopwatch.StartNew();
            using var session = new OrtNanoVsrUpscaler(path, VideoSrBackend.Gpu, 192, default);
            double setupMs = timer.Elapsed.TotalMilliseconds;
            var input = Enumerable.Range(0, 5).Select(_ => new SKBitmap(new SKImageInfo(setting.Item1, setting.Item2, SKColorType.Bgra8888, SKAlphaType.Opaque))).ToArray();
            try
            {
                foreach (var frame in input) frame.Erase(SKColors.SteelBlue);
                timer.Restart(); Run(); double coldMs = timer.Elapsed.TotalMilliseconds;
                var times = new double[3];
                long allocated = GC.GetTotalAllocatedBytes();
                for (int i = 0; i < times.Length; i++) { timer.Restart(); Run(); times[i] = timer.Elapsed.TotalMilliseconds; }
                Array.Sort(times);
                var record = new { width = setting.Item1, height = setting.Item2, scale = setting.Item3,
                    temporalFrames = 5, device = session.Device.Name, setupMs, coldWindowMs = coldMs,
                    warmMsPerFrame = times[1] / 5, fps = 5000 / times[1], allocatedBytesPerFrame = (GC.GetTotalAllocatedBytes() - allocated) / 15 };
                records.Add(record); Console.WriteLine(JsonSerializer.Serialize(record));
            }
            finally { foreach (var frame in input) frame.Dispose(); }

            void Run() { foreach (var frame in session.UpscaleSequence(input, setting.Item3, default)) frame.Dispose(); }
        }
        File.WriteAllText(Path.Combine(workDir, "nano-benchmark.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
