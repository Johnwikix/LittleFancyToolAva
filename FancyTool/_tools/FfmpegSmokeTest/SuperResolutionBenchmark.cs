using System.Diagnostics;
using System.Text.Json;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    // Measures the complete bitmap path, including tensor conversion, tile overlap and readback.
    // Cold compilation is recorded separately from the median of three warm frames.
    private static int RunSrBenchmark(string workDir)
    {
        using var http = new HttpClient();
        var downloader = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
        var video = new VideoSuperResolutionService(downloader, NullLogger<VideoSuperResolutionService>.Instance);
        using var source = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(source))
        {
            canvas.Clear(new SKColor(64, 112, 170));
            using var paint = new SKPaint { Color = SKColors.Orange, IsAntialias = true };
            for (int i = 0; i < 30; i++) canvas.DrawCircle(i * 23, i * 11, 15, paint);
        }
        var records = new List<object>();
        foreach (var model in Enum.GetValues<VideoSrModel>())
        {
            var clock = Stopwatch.StartNew();
            using var session = video.CreateSessionAsync(new VideoTranscodeOptions
            {
                SuperResolutionModel = model, SuperResolutionBackend = VideoSrBackend.Gpu,
                }).GetAwaiter().GetResult();
            double setup = clock.Elapsed.TotalMilliseconds;
            Measure("video", model, session.Device.Name, setup, () => session.Upscale(source, 2, default));

        }
        File.WriteAllText(Path.Combine(workDir, "sr-benchmark.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        return 0;

        void Measure(string pipeline, VideoSrModel model, string device, double setup, Func<SKBitmap> run)
        {
            var clock = Stopwatch.StartNew();
            using (var cold = run()) cold.GetPixel(0, 0);
            double coldMs = clock.Elapsed.TotalMilliseconds;
            var times = new double[3];
            long allocated = GC.GetTotalAllocatedBytes();
            for (int i = 0; i < times.Length; i++)
            {
                clock.Restart();
                using var output = run();
                times[i] = clock.Elapsed.TotalMilliseconds;
                if (output.Width != 1280 || output.Height != 720) throw new InvalidOperationException("Benchmark output size incorrect");
            }
            Array.Sort(times);
            long bytes = (GC.GetTotalAllocatedBytes() - allocated) / 3;
            records.Add(new { pipeline, model = model.ToString(), device, width = 640, height = 360, scale = 2, setupMs = setup, coldMs, warmMedianMs = times[1], allocatedBytesPerFrame = bytes });
            Console.WriteLine($"BENCH {pipeline} {model} {device}: setup={setup:F0}ms cold={coldMs:F0}ms warm={times[1]:F0}ms allocated={bytes / 1048576.0:F1}MB");
        }
    }
}
