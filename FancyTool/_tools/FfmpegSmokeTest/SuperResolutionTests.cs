using System.Diagnostics;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using FFmpeg.AutoGen;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunSuperResolutionCases(string source, string workDir)
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        using var http = new HttpClient();
        var downloader = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
        var sr = new VideoSuperResolutionService(downloader, NullLogger<VideoSuperResolutionService>.Instance);
        var service = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance, sr);
        sr.DeviceChanged += device => Console.WriteLine($"  actual device: {device.Name}, fallback: {device.FallbackReason ?? "none"}");
        var clock = Stopwatch.StartNew();
        RunExtra("Windows ML GPU video 2x, audio and trim", () =>
        {
            string output = Path.Combine(workDir, "sr-gpu.mp4");
            service.TranscodeAsync(source, output, new VideoTranscodeOptions
            {
                SuperResolutionEnabled = true, SuperResolutionBackend = VideoSrBackend.Gpu,
                SuperResolutionAllowCpuFallback = false, TrimStartSeconds = 0.4, TrimEndSeconds = 0.7, Preset = PresetLevel.Ultrafast
            }).GetAwaiter().GetResult();
            var probe = service.ProbeAsync(output).GetAwaiter().GetResult()!;
            if (probe.Width != 320 || probe.Height != 240 || !probe.HasAudio || Math.Abs(probe.DurationMs - 300) > 100 || Math.Abs(probe.Fps - 10) > 0.01)
                throw new InvalidOperationException($"Wrong enhanced clip metadata: {probe}");
            var before = service.GetPreviewAsync(source, 0.4).GetAwaiter().GetResult()!;
            var after = service.GetPreviewAsync(output, 0).GetAwaiter().GetResult()!;
            if (after.Width <= before.Width || after.Pixels.Distinct().Count() < 20) throw new InvalidOperationException("Enhanced output frame missing");
        });
        Console.WriteLine($"GPU test elapsed: {clock.Elapsed.TotalSeconds:F2}s");
        clock.Restart();
        RunExtra("Intel NPU video, crop and 4x", () =>
        {
            var actual = new List<VideoSrDevice>();
            sr.DeviceChanged += actual.Add;
            string output = Path.Combine(workDir, "sr-npu.mp4");
            service.TranscodeAsync(source, output, new VideoTranscodeOptions
            {
                SuperResolutionEnabled = true, SuperResolutionBackend = VideoSrBackend.PreferNpu,
                SuperResolutionAllowCpuFallback = false, SuperResolutionScale = 4,
                CropEnabled = true, CropLeft = 4, CropRight = 4, CropTop = 2, CropBottom = 2,
                TrimStartSeconds = 0.4, TrimEndSeconds = 0.5, AudioCodec = AudioCodec.None, Preset = PresetLevel.Ultrafast
            }).GetAwaiter().GetResult();
            var probe = service.ProbeAsync(output).GetAwaiter().GetResult()!;
            if (probe.Width != 608 || probe.Height != 464) throw new InvalidOperationException($"Wrong NPU output dimensions: {probe.Width}x{probe.Height}");
            if (Math.Abs(probe.Fps - 10) > 0.01) throw new InvalidOperationException($"Wrong NPU output frame rate: {probe.Fps}");
            if (actual.Count == 0 || !actual[^1].Name.StartsWith("NPU")) throw new InvalidOperationException("NPU request fell back to another device");
        });
        Console.WriteLine($"NPU test elapsed: {clock.Elapsed.TotalSeconds:F2}s");
        clock.Restart();
        RunExtra("image NPU inference and transparency", () =>
        {
            var preferences = new AppPreferences { UseSuperResolutionNpu = true, SuperResolutionTileSizeIndex = 0 };
            using var imageSr = new SuperResolutionService(NullLogger<SuperResolutionService>.Instance, preferences, downloader);
            imageSr.WarmupTask.GetAwaiter().GetResult();
            using var bitmap = new SKBitmap(new SKImageInfo(24, 18, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(new SKColor(80, 120, 170, 128));
            using var output = imageSr.UpscaleAsync(bitmap, SuperResolutionModel.RealEsrganGeneralX4V3, 2).GetAwaiter().GetResult();
            if (output.Width != 48 || output.Height != 36 || output.GetPixel(20, 15).Alpha is < 120 or > 136)
                throw new InvalidOperationException("Image NPU size/alpha incorrect");
            if (imageSr.InferenceDevice?.Name.StartsWith("NPU") != true) throw new InvalidOperationException($"Image NPU fallback: {imageSr.InferenceDevice}");
            Console.WriteLine($"  image device: {imageSr.InferenceDevice.Name}");
        });
        Console.WriteLine($"Image NPU test elapsed: {clock.Elapsed.TotalSeconds:F2}s");
        RunExtra("native 2x SPAN image alpha and video", () =>
        {
            using var image = new SuperResolutionService(NullLogger<SuperResolutionService>.Instance, new AppPreferences { SuperResolutionTileSizeIndex = 0 }, downloader);
            image.WarmupTask.GetAwaiter().GetResult();
            using var input = new SKBitmap(new SKImageInfo(31, 23, SKColorType.Bgra8888, SKAlphaType.Premul));
            input.Erase(new SKColor(90, 130, 170, 128));
            using var output = image.UpscaleAsync(input, SuperResolutionModel.LiveActionSpanX2, 2).GetAwaiter().GetResult();
            if (output.Width != 62 || output.Height != 46 || output.GetPixel(30, 20).Alpha is < 120 or > 136)
                throw new InvalidOperationException("Native 2x image size or alpha incorrect");
            string videoOutput = Path.Combine(workDir, "sr-native2x.mp4");
            service.TranscodeAsync(source, videoOutput, new VideoTranscodeOptions
            {
                SuperResolutionEnabled = true, SuperResolutionModel = SuperResolutionModel.LiveActionSpanX2,
                SuperResolutionBackend = VideoSrBackend.Gpu, SuperResolutionAllowCpuFallback = false,
                TrimStartSeconds = 0.4, TrimEndSeconds = 0.6, Preset = PresetLevel.Ultrafast
            }).GetAwaiter().GetResult();
            var probe = service.ProbeAsync(videoOutput).GetAwaiter().GetResult()!;
            if (probe.Width != 320 || probe.Height != 240 || !probe.HasAudio || Math.Abs(probe.Fps - 10) > 0.01)
                throw new InvalidOperationException("Native 2x video output incorrect");
        });
        RunExtra("GPU fps preprocessing and output scaling", () =>
        {
            string output = Path.Combine(workDir, "sr-filtered.mp4");
            service.TranscodeAsync(source, output, new VideoTranscodeOptions
            {
                SuperResolutionEnabled = true, SuperResolutionBackend = VideoSrBackend.Gpu,
                SuperResolutionAllowCpuFallback = false, TrimStartSeconds = 0.4, TrimEndSeconds = 1,
                FpsMode = FpsMode.Fixed, FpsValue = 5, ScaleMode = ScaleMode.Exact, ScaleWidth = 200, ScaleHeight = 150,
                Preset = PresetLevel.Ultrafast
            }).GetAwaiter().GetResult();
            var probe = service.ProbeAsync(output).GetAwaiter().GetResult()!;
            if (probe.Width != 200 || probe.Height != 150 || Math.Abs(probe.Fps - 5) > 0.01 || Math.Abs(probe.DurationMs - 600) > 150)
                throw new InvalidOperationException($"Pre/post filter settings were applied incorrectly: {probe}");
        });
        RunExtra("CPU selection and canceled enhancement cleanup", () =>
        {
            string output = Path.Combine(workDir, "sr-cpu.mp4");
            service.TranscodeAsync(source, output, new VideoTranscodeOptions
            {
                SuperResolutionEnabled = true, SuperResolutionBackend = VideoSrBackend.Cpu,
                TrimStartSeconds = 0.4, TrimEndSeconds = 0.5, AudioCodec = AudioCodec.None, Preset = PresetLevel.Ultrafast
            }).GetAwaiter().GetResult();
            if (service.ProbeAsync(output).GetAwaiter().GetResult()!.Width != 320) throw new InvalidOperationException("CPU enhancement output missing");
            using var ct = new CancellationTokenSource();
            string canceled = Path.Combine(workDir, "sr-canceled-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                service.TranscodeAsync(source, canceled, new VideoTranscodeOptions
                {
                    SuperResolutionEnabled = true, SuperResolutionBackend = VideoSrBackend.Gpu,
                    SuperResolutionAllowCpuFallback = false, Preset = PresetLevel.Ultrafast
                }, ct: ct.Token, preview: new CancelPreview(ct)).GetAwaiter().GetResult();
                throw new InvalidOperationException("Cancellation was ignored");
            }
            catch (OperationCanceledException) { }
            if (File.Exists(canceled) || File.Exists(Path.ChangeExtension(canceled, "tmp.mp4"))) throw new InvalidOperationException("Canceled enhancement left output files");
        });
        Console.WriteLine($"SR smoke tests: {_runCount} cases, {_failures} failures");
        return _failures == 0 ? 0 : 1;
    }

    private sealed class CancelPreview(CancellationTokenSource source) : IProgress<VideoPreviewFrame>
    {
        public void Report(VideoPreviewFrame frame) => source.Cancel();
    }
}
