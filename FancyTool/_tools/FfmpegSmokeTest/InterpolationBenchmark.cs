using System.Diagnostics;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunInterpolationBenchmark(string workDir, bool full)
        => Task.Run(() => RunInterpolationBenchmarkCore(workDir, full)).GetAwaiter().GetResult();

    private static int RunInterpolationBenchmarkCore(string workDir, bool full)
    {
        var measurements = new List<string>();
        foreach (var model in Enum.GetValues<VideoInterpolationModel>())
        foreach (var backend in full ? new[] { VideoSrBackend.Gpu } : new[] { VideoSrBackend.Gpu, VideoSrBackend.PreferNpu })
        foreach (var (width, height) in full ? new[] { (1280, 720), (1920, 1080), (3840, 2160) } : new[] { (160, 120) })
        {
            string path = Path.Combine(AppPaths.ModelsDirectory, "Interpolation", VideoInterpolationService.GetModel(model).FileName);
            var clock = Stopwatch.StartNew();
            try
            {
                using var session = new OrtVideoFrameInterpolator(path, backend, width, height, CancellationToken.None);
                double createMs = clock.Elapsed.TotalMilliseconds;
                using var first = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                using var second = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                using var output = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                first.Erase(SKColors.DarkSlateBlue); second.Erase(SKColors.DarkSlateBlue);
                using (var canvas = new SKCanvas(first)) canvas.DrawRect(20, 20, 40, 40, new SKPaint { Color = SKColors.Orange });
                using (var canvas = new SKCanvas(second)) canvas.DrawRect(30, 20, 40, 40, new SKPaint { Color = SKColors.Orange });
                session.SetFrames(first, second, CancellationToken.None);
                clock.Restart();
                session.Interpolate(0.5, output, CancellationToken.None);
                double warmMs = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                int count = full ? 2 : 3;
                for (int i = 0; i < count; i++) session.Interpolate((i + 1d) / (count + 1), output, CancellationToken.None);
                string result = $"{model} {session.Device.Name} {width}x{height}: create={createMs:F0}ms, first={warmMs:F0}ms, mean={clock.Elapsed.TotalMilliseconds / count:F1}ms, fps={count / clock.Elapsed.TotalSeconds:F2}";
                Console.WriteLine(result);
                measurements.Add(result);
                if (!full) using (var image = SKImage.FromBitmap(output)) using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                    File.WriteAllBytes(Path.Combine(workDir, $"{model}-{backend}.png"), data.ToArray());
            }
            catch (Exception ex)
            {
                string result = $"{model} {backend} {width}x{height}: unavailable after {clock.Elapsed.TotalSeconds:F1}s: {ex.Message}";
                Console.WriteLine(result[..Math.Min(result.Length, 1800)]);
                measurements.Add(result);
            }
        }
        File.WriteAllLines(Path.Combine(workDir, "interpolation-benchmark.txt"), measurements);
        return measurements.Any(s => s.Contains("GPU") && s.Contains("fps=")) ? 0 : 1;
    }

    private static int RunInterpolationOrderBenchmark(string workDir) => Task.Run(() =>
    {
        using var http = new HttpClient();
        var downloads = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
        var sr = new VideoSuperResolutionService(downloads, NullLogger<VideoSuperResolutionService>.Instance,
            new AppPreferences { SuperResolutionTileSizeIndex = 0 });
        string model = Path.Combine(AppPaths.ModelsDirectory, "Interpolation", VideoInterpolationService.GetModel(VideoInterpolationModel.Rife425Lite).FileName);
        using var low = new OrtVideoFrameInterpolator(model, VideoSrBackend.Gpu, 160, 120, CancellationToken.None);
        using var high = new OrtVideoFrameInterpolator(model, VideoSrBackend.Gpu, 320, 240, CancellationToken.None);
        using var first = new SKBitmap(160, 120, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var second = new SKBitmap(160, 120, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var generated = new SKBitmap(160, 120, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var generatedHigh = new SKBitmap(320, 240, SKColorType.Bgra8888, SKAlphaType.Opaque);
        first.Erase(SKColors.DarkSlateBlue); second.Erase(SKColors.DarkSlateBlue);
        using var brush = new SKPaint { Color = SKColors.Orange };
        using (var canvas = new SKCanvas(first)) canvas.DrawRect(20, 20, 40, 40, brush);
        using (var canvas = new SKCanvas(second)) canvas.DrawRect(30, 20, 40, 40, brush);
        var results = new List<string>();
        foreach (var srModel in new[] { VideoSrModel.Anime4KCnnS, VideoSrModel.FsrcnnxS })
        {
            using var upscaler = sr.CreateSessionAsync(new VideoTranscodeOptions { SuperResolutionModel = srModel,
                SuperResolutionBackend = VideoSrBackend.Gpu, }).GetAwaiter().GetResult();
            using var firstHigh = upscaler.Upscale(first, 2, CancellationToken.None);
            void Before()
            {
                low.SetFrames(first, second, CancellationToken.None); low.Interpolate(.5, generated, CancellationToken.None);
                using var original = upscaler.Upscale(first, 2, CancellationToken.None);
                using var interpolated = upscaler.Upscale(generated, 2, CancellationToken.None);
            }
            void After()
            {
                using var next = upscaler.Upscale(second, 2, CancellationToken.None);
                high.SetFrames(firstHigh, next, CancellationToken.None); high.Interpolate(.5, generatedHigh, CancellationToken.None);
            }
            Before(); After();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) Before();
            double before = clock.Elapsed.TotalMilliseconds / 5;
            clock.Restart();
            for (int i = 0; i < 5; i++) After();
            double after = clock.Elapsed.TotalMilliseconds / 5;
            string result = $"{srModel}, 160x120 -> 320x240, 2x fps: interpolation then SR={before:F1}ms/source interval; SR then interpolation={after:F1}ms/source interval";
            Console.WriteLine(result); results.Add(result);
        }
        File.WriteAllLines(Path.Combine(workDir, "interpolation-order-benchmark.txt"), results);
        return 0;
    }).GetAwaiter().GetResult();
}
