using FancyToolAva.Models;
using FancyToolAva.Services;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunVideoPipelineIntegration(string source, string workDir)
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        using var http = new HttpClient();
        var downloads = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
        using var sr = new VideoSuperResolutionService(downloads, NullLogger<VideoSuperResolutionService>.Instance,
            new AppPreferences { SuperResolutionTileSizeIndex = 0 });
        var interpolation = new VideoInterpolationService(downloads, NullLogger<VideoInterpolationService>.Instance);
        using var interpolationLifetime = TestServiceLifetime(interpolation);
        var service = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance, sr, interpolation);
        foreach (var order in Enum.GetValues<VideoFrameProcessingOrder>())
        foreach (int multiplier in new[] { 2, 4, 6 })
            RunExtra($"queued GPU {order} {multiplier}x: clock, audio, final preview and statistics", () =>
            {
                var options = Options();
                options.FrameProcessingOrder = order;
                options.InterpolationMultiplier = multiplier;
                var preview = new LastPreview();
                var statistics = new List<VideoEncodingProgress>();
                string output = Path.Combine(workDir, $"{order}-{multiplier}.mp4");
                service.TranscodeAsync(source, output, options, preview: preview,
                    statistics: new CaptureProgress<VideoEncodingProgress>(statistics)).GetAwaiter().GetResult();
                AssertInterpolated(service, output, 10 * multiplier, 4 * multiplier, .4, true);
                var probe = service.ProbeAsync(output).GetAwaiter().GetResult()!;
                if (probe.Width != 320 || probe.Height != 240 || preview.Last == null || preview.Last.PositionSeconds < .7)
                    throw new InvalidOperationException("Dimensions or preview changed");
                if (statistics.Last() is not { IsTotalEstimated: false } final || final.ProcessedFrames != 4 * multiplier || final.TotalFrames != 4 * multiplier)
                    throw new InvalidOperationException("Final statistics were reordered");
            });

        RunExtra("queued RIFE unaligned trim and one-frame tail", () =>
        {
            foreach (var (start, end, count) in new[] { (.353, .953, 24), (.4, .5, 4) })
            {
                var options = Options(); options.SuperResolutionEnabled = false;
                options.TrimStartSeconds = start; options.TrimEndSeconds = end;
                string output = Path.Combine(workDir, $"tail-{count}.mp4");
                service.TranscodeAsync(source, output, options).GetAwaiter().GetResult();
                AssertInterpolated(service, output, 40, count, end - start, true);
            }
        });
        RunExtra("queued borrowed BGRA buffers with padded row strides", () =>
        {
            string unaligned = Path.Combine(workDir, "unaligned.avi");
            WriteTestAvi(unaligned, 170, 98, 10, 1);
            foreach (var order in new[] { VideoFrameProcessingOrder.InterpolationThenUpscale, VideoFrameProcessingOrder.UpscaleThenInterpolation })
            {
                var options = Options(); options.FrameProcessingOrder = order;
                options.TrimStartSeconds = 0; options.TrimEndSeconds = .6;
                string output = Path.Combine(workDir, order + "-stride.mp4");
                service.TranscodeAsync(unaligned, output, options).GetAwaiter().GetResult();
                AssertInterpolated(service, output, 40, 24, .6, true);
                var probe = service.ProbeAsync(output).GetAwaiter().GetResult()!;
                if (probe.Width != 340 || probe.Height != 196) throw new InvalidOperationException("Stride fixture dimensions changed");
            }
        });
        RunExtra("queued GPU GIF and two-pass software encoding", () =>
        {
            var options = Options(); options.SuperResolutionEnabled = false;
            options.Container = VideoContainer.Gif; options.VideoCodec = VideoCodec.Gif; options.AudioCodec = AudioCodec.None;
            options.InterpolationMultiplier = 2; // GIF 的百分之一秒时钟能精确表达 20 fps。
            options.HardwareBackend = HardwareBackend.Software; options.GifWidth = 120;
            string output = Path.Combine(workDir, "queued.gif");
            service.TranscodeAsync(source, output, options).GetAwaiter().GetResult();
            AssertInterpolated(service, output, 20, 8, .4, false);
            options = Options(); options.HardwareBackend = HardwareBackend.Software;
            options.TwoPassEnabled = true; options.RateControl = RateControlMode.Bitrate;
            output = Path.Combine(workDir, "queued-two-pass.mp4");
            service.TranscodeAsync(source, output, options).GetAwaiter().GetResult();
            AssertInterpolated(service, output, 40, 16, .4, true);
        });
        RunExtra("queued CPU motion compensation, scene cut and VFR", () =>
        {
            var options = Options(); options.InterpolationMode = VideoInterpolationMode.MotionCompensation;
            string output = Path.Combine(workDir, "motion.mp4");
            service.TranscodeAsync(source, output, options).GetAwaiter().GetResult();
            AssertInterpolated(service, output, 40, 16, .4, true);
            string cuts = Path.Combine(workDir, "cuts.avi");
            WriteTestAvi(cuts, 160, 120, 10, 2);
            FillAviFrames(cuts, (pixels, frame) => pixels.Fill(frame < 10 ? (byte)0 : (byte)255));
            options = Options(); options.SuperResolutionEnabled = false;
            options.TrimStartSeconds = 0; options.TrimEndSeconds = 2;
            service.TranscodeAsync(cuts, output, options).GetAwaiter().GetResult();
            if (service.GetPreviewAsync(output, .975).GetAwaiter().GetResult()!.Pixels[0] > 10)
                throw new InvalidOperationException("Scene cut generated a transition");
            string compressed = Path.Combine(workDir, "compressed.mp4"), vfr = Path.Combine(workDir, "vfr.mp4");
            service.TranscodeAsync(source, compressed, new VideoTranscodeOptions { Preset = PresetLevel.Ultrafast }).GetAwaiter().GetResult();
            WriteVariableSource(compressed, vfr);
            options.TrimEndSeconds = 1;
            double rate = service.ProbeAsync(vfr).GetAwaiter().GetResult()!.Fps * 4;
            service.TranscodeAsync(vfr, output, options).GetAwaiter().GetResult();
            AssertInterpolated(service, output, rate, (int)Math.Ceiling(rate - .000001), 1, true);
        });
        RunExtra("queued cancellation, producer failure, encoder failure and session reuse", () =>
        {
            foreach (string fault in new[] { "cancel", "startup-cancel", "sr", "encoder" })
            {
                using var cancelled = new CancellationTokenSource();
                if (fault == "startup-cancel") cancelled.CancelAfter(1);
                string output = Path.Combine(workDir, $"failure-{fault}.mp4");
                var failing = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance,
                    fault == "sr" ? new FailingSrService(sr) : sr, interpolation);
                try
                {
                    failing.TranscodeAsync(source, output, Options(), ct: cancelled.Token,
                        preview: fault == "cancel" ? new CancelPreview(cancelled) : null,
                        progress: fault == "encoder" ? new FailingProgress() : null).GetAwaiter().GetResult();
                    throw new InvalidOperationException("Fault did not stop the pipeline: " + fault);
                }
                catch (OperationCanceledException) when (fault.Contains("cancel")) { }
                catch (InvalidDataException ex) when (ex.Message == "pipeline-test-fault") { }
                if (File.Exists(output) || Directory.GetFiles(workDir, $"*failure-{fault}*.tmp*").Length != 0)
                    throw new InvalidOperationException("Failed pipeline retained output");
                service.TranscodeAsync(source, output, Options()).GetAwaiter().GetResult();
                AssertInterpolated(service, output, 40, 16, .4, true);
            }
        });
        RunExtra("active tiled GPU cancellation joins workers before cached session reuse", () =>
        {
            string large = Path.Combine(workDir, "tiled-cancel.avi");
            WriteTestAvi(large, 1280, 720, 4, .5);
            var options = Options();
            options.InterpolationMode = VideoInterpolationMode.None;
            options.TrimStartSeconds = 0; options.TrimEndSeconds = .5;
            using var cancelled = new CancellationTokenSource();
            var cancelling = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance, new FailingSrService(sr, cancelled));
            string output = Path.Combine(workDir, "active-tiled-cancel.mp4");
            try
            {
                cancelling.TranscodeAsync(large, output, options, ct: cancelled.Token).GetAwaiter().GetResult();
                throw new InvalidOperationException("Tiled cancellation ignored");
            }
            catch (OperationCanceledException) { }
            if (File.Exists(output) || Directory.GetFiles(workDir, "*active-tiled-cancel*.tmp*").Length != 0)
                throw new InvalidOperationException("Tiled cancellation retained output");
            service.TranscodeAsync(large, output, options).GetAwaiter().GetResult();
            AssertInterpolated(service, output, 4, 2, .5, true);
        });
        RunExtra("deterministic repeated SR frames reuse pixels and preserve every timestamp", () =>
        {
            string repeated = Path.Combine(workDir, "repeated.avi");
            WriteTestAvi(repeated, 160, 120, 8, 1);
            FillAviFrames(repeated, (pixels, index) => pixels.Fill(index < 3 ? (byte)30 : index < 5 ? (byte)180 : (byte)60));
            var reference = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance, new NoFrameReuseService(sr), interpolation);
            string? previousProfile = Environment.GetEnvironmentVariable("FANCYTOOL_VIDEO_PROFILE");
            try
            {
                foreach (var mode in new[] { VideoInterpolationMode.None, VideoInterpolationMode.Rife })
                {
                    var options = Options();
                    options.InterpolationMode = mode;
                    options.InterpolationSceneDetection = false;
                    options.TrimStartSeconds = 0;
                    options.TrimEndSeconds = 1;
                    var hashes = new List<string>();
                    var controlHashes = new List<string>();
                    var counts = new List<Dictionary<string, long>>();
                    foreach (bool reuse in new[] { false, true })
                    {
                        string profile = Path.Combine(workDir, $"repeated-{mode}-{reuse}");
                        Directory.CreateDirectory(profile);
                        Environment.SetEnvironmentVariable("FANCYTOOL_VIDEO_PROFILE", profile);
                        string output = Path.Combine(profile, "output.mp4");
                        (reuse ? service : reference).TranscodeAsync(repeated, output, options).GetAwaiter().GetResult();
                        var times = ReadVideoTimes(output, reuse ? hashes : controlHashes);
                        int expected = mode == VideoInterpolationMode.None ? 8 : 32;
                        if (times.Count != expected || times.Where((t, i) => Math.Abs(t - i / (double)expected) > .0001).Any())
                            throw new InvalidOperationException("Repeated-frame timestamps changed");
                        string csv = Directory.GetFiles(profile, "pipeline-*.csv").OrderBy(File.GetLastWriteTimeUtc).Last();
                        counts.Add(File.ReadLines(csv).Skip(1).Select(line => line.Split(','))
                            .ToDictionary(parts => parts[0], parts => long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
                    }
                    if (!hashes.SequenceEqual(controlHashes) || counts[1]["SrFrameReuse"] == 0
                        || counts[1]["SrNativeRun"] >= counts[0]["SrNativeRun"])
                        throw new InvalidOperationException("Repeated-frame reuse changed pixels or did not reduce inference");
                }
            }
            finally { Environment.SetEnvironmentVariable("FANCYTOOL_VIDEO_PROFILE", previousProfile); }
        });
        Console.WriteLine($"Pipeline integration: {_runCount} cases, {_failures} failures");
        return _failures == 0 ? 0 : 1;

        static VideoTranscodeOptions Options() => new()
        {
            SuperResolutionEnabled = true, SuperResolutionModel = VideoSrModel.FsrcnnxM,
            SuperResolutionBackend = VideoSrBackend.Gpu, SuperResolutionUseFp16 = true,
            InterpolationMode = VideoInterpolationMode.Rife, InterpolationModel = VideoInterpolationModel.Rife425Lite,
            InterpolationBackend = VideoSrBackend.Gpu, InterpolationAllowCpuFallback = false,
            InterpolationMultiplier = 4, TrimStartSeconds = .4, TrimEndSeconds = .8,
            HardwareBackend = HardwareBackend.Nvidia, Preset = PresetLevel.Ultrafast, AudioCodec = AudioCodec.Aac
        };
    }

    private sealed class NoFrameReuseService(IVideoSuperResolutionService inner) : IVideoSuperResolutionService
    {
        public event Action<ModelDownloadProgress>? DownloadProgressChanged { add => inner.DownloadProgressChanged += value; remove => inner.DownloadProgressChanged -= value; }
        public event Action<VideoSrDevice>? DeviceChanged { add => inner.DeviceChanged += value; remove => inner.DeviceChanged -= value; }
        public bool IsModelAvailable(VideoSrModel model) => inner.IsModelAvailable(model);
        public Task EnsureModelAsync(VideoSrModel model, CancellationToken ct = default) => inner.EnsureModelAsync(model, ct);
        public Task InstallAccelerationAsync(CancellationToken ct = default) => inner.InstallAccelerationAsync(ct);
        public async Task<IVideoFrameUpscaler> CreateSessionAsync(VideoTranscodeOptions options, CancellationToken ct = default)
            => new NoFrameReuseUpscaler(await inner.CreateSessionAsync(options, ct));
    }
    private sealed class NoFrameReuseUpscaler(IVideoFrameUpscaler inner) : IVideoFrameUpscaler, IVideoFrameDestinationUpscaler
    {
        public VideoSrDevice Device => inner.Device;
        public SKBitmap Upscale(SKBitmap source, int scale, CancellationToken ct) => inner.Upscale(source, scale, ct);
        public bool TryUpscaleInto(SKBitmap source, int scale, SKBitmap destination, CancellationToken ct)
            => inner is IVideoFrameDestinationUpscaler writer && writer.TryUpscaleInto(source, scale, destination, ct);
        public void Dispose() => inner.Dispose();
    }

    private sealed class FailingProgress : IProgress<double>
    {
        public void Report(double value) { if (value > 0 && value < 1) throw new InvalidDataException("pipeline-test-fault"); }
    }
    private sealed class FailingSrService(IVideoSuperResolutionService inner, CancellationTokenSource? cancel = null) : IVideoSuperResolutionService
    {
        public event Action<ModelDownloadProgress>? DownloadProgressChanged { add => inner.DownloadProgressChanged += value; remove => inner.DownloadProgressChanged -= value; }
        public event Action<VideoSrDevice>? DeviceChanged { add => inner.DeviceChanged += value; remove => inner.DeviceChanged -= value; }
        public bool IsModelAvailable(VideoSrModel model) => inner.IsModelAvailable(model);
        public Task EnsureModelAsync(VideoSrModel model, CancellationToken ct = default) => inner.EnsureModelAsync(model, ct);
        public Task InstallAccelerationAsync(CancellationToken ct = default) => inner.InstallAccelerationAsync(ct);
        public async Task<IVideoFrameUpscaler> CreateSessionAsync(VideoTranscodeOptions options, CancellationToken ct = default)
            => new FailingUpscaler(await inner.CreateSessionAsync(options, ct), cancel);
    }
    private sealed class FailingUpscaler(IVideoFrameUpscaler inner, CancellationTokenSource? cancel = null) : IVideoFrameUpscaler
    {
        private int _calls;
        public VideoSrDevice Device => inner.Device;
        public SKBitmap Upscale(SKBitmap source, int scale, CancellationToken ct)
        {
            int call = ++_calls;
            if (cancel != null && call == 1) cancel.CancelAfter(10);
            if (cancel == null && call == 2) throw new InvalidDataException("pipeline-test-fault");
            return inner.Upscale(source, scale, ct);
        }
        public void Dispose() => inner.Dispose();
    }
}
