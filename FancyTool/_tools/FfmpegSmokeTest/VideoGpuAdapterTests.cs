using System.Text.Json;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunVideoGpuAdapters(string workDir, string[] args)
    {
        using var http = new HttpClient();
        var downloads = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
        using var service = new VideoSuperResolutionService(downloads, NullLogger<VideoSuperResolutionService>.Instance,
            new AppPreferences { SuperResolutionTileSizeIndex = 0 });
        var records = new List<object>();
        bool passed = true;
        foreach (var model in VideoSrModelCatalog.Models.Where(m => !args.Contains("--model")
            || m.Model.ToString() == args[Array.IndexOf(args, "--model") + 1]))
        foreach (bool half in model.SupportsFp16 ? new[] { false, true } : new[] { false })
        {
            int window = model.RequiresDirectMl ? 384 : 192;
            string path = Path.Combine(AppPaths.ModelsDirectory, "VideoSr", model.GetGraph(half).FileName);
            bool cpuReference = model.Model == VideoSrModel.RavuLiteR3;
            var legacy = new OrtVideoSrTileModel(path, cpuReference ? VideoSrBackend.Cpu : model.RequiresDirectMl ? VideoSrBackend.DirectMl : VideoSrBackend.VendorGpu,
                window, default, 2, !cpuReference, half);
            using var reference = new CascadedVideoFrameUpscaler(new VideoFrameUpscaler(legacy, legacy.Device, window, 2));
            using var adapted = service.CreateSessionAsync(new VideoTranscodeOptions
            { SuperResolutionEnabled = true, SuperResolutionModel = model.Model, SuperResolutionUseFp16 = half }).GetAwaiter().GetResult();
            foreach (var (width, height) in new[] { (31, 17), (193, 129) })
            {
                var frames = CreateCandidateFrames(width, height, 1);
                try
                {
                    foreach (int scale in new[] { 2, 4 })
                    {
                        using var before = reference.Upscale(frames[0], scale, default);
                        using var after = adapted.Upscale(frames[0], scale, default);
                        var difference = PixelDifference(before, after);
                        // RAVU R3 原始厂商图在此卡无法建立会话；使用既有 .02/.001 浮点参考容差折算像素。
                        bool equal = cpuReference ? difference.max <= 5 && difference.mean <= .255 : difference.max == 0;
                        passed &= equal;
                        var record = new { model = model.Model.ToString(), half, width, height, scale, cpuReference,
                            adapted.Device, gpuPixels = adapted.Device.UsesGpuPixels, difference.max, difference.mean, difference.different, passed = equal };
                        Console.WriteLine(JsonSerializer.Serialize(record)); records.Add(record);
                    }
                }
                finally { foreach (var frame in frames) frame.Dispose(); }
            }
        }
        foreach (var model in args.Contains("--model") ? Array.Empty<VideoInterpolationModel>() : Enum.GetValues<VideoInterpolationModel>())
        foreach (var (width, height) in new[] { (17, 9), (160, 120), (640, 360), (1920, 1080) })
        {
            string path = Path.Combine(AppPaths.ModelsDirectory, "Interpolation", VideoInterpolationService.GetModel(model).FileName);
            var frames = CreateCandidateFrames(width, height, 2);
            try
            {
                using var reference = new OrtVideoFrameInterpolator(path, VideoSrBackend.Gpu, width, height, default);
                using var adapted = new OrtBgraVideoFrameInterpolator(path, width, height, default, null);
                using var before = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                using var after = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                for (int repeat = 0; repeat < 3; repeat++)
                {
                    reference.SetFrames(frames[0], frames[1], default);
                    adapted.SetFrames(frames[0], frames[1], default);
                    foreach (double amount in new[] { 1d / 6, .25, .5, .75, 5d / 6 })
                    {
                        reference.Interpolate(amount, before, default);
                        adapted.Interpolate(amount, after, default);
                        var difference = PixelDifference(before, after);
                        bool equal = difference.max == 0;
                        passed &= equal;
                        var record = new { model = model.ToString(), width, height, amount, repeat,
                            difference.max, difference.mean, difference.different, passed = equal };
                        Console.WriteLine(JsonSerializer.Serialize(record));
                        records.Add(record);
                    }
                }
                int pixel = Math.Min(height - 1, height / 2 + 1) * frames[0].RowBytes + Math.Min(width - 1, width / 2 + 1) * 4;
                frames[0].GetPixelSpan()[pixel] ^= 31;
                reference.SetFrames(frames[0], frames[1], default);
                adapted.SetFrames(frames[0], frames[1], default);
                reference.Interpolate(.5, before, default);
                adapted.Interpolate(.5, after, default);
                var changed = PixelDifference(before, after);
                passed &= changed.max == 0;
                records.Add(new { model = model.ToString(), width, height, phase = "changed-pixel", changed.max,
                    changed.mean, changed.different, passed = changed.max == 0 });
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
        File.WriteAllText(Path.Combine(workDir, "gpu-adapters.json"), JsonSerializer.Serialize(new { passed, records }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    private static (int max, double mean, int different) PixelDifference(SKBitmap first, SKBitmap second)
    {
        var a = first.GetPixelSpan(); var b = second.GetPixelSpan();
        if (a.Length != b.Length) throw new InvalidOperationException("Bitmap shape changed");
        int max = 0, different = 0; long total = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]); max = Math.Max(max, d); total += d; if (d > 0) different++;
        }
        return (max, total / (double)a.Length, different);
    }
}
