using System.Diagnostics;
using System.Text.Json;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunRealVideoSrCase(string workDir, string[] args)
    {
        string Argument(string key) => args.ElementAtOrDefault(Array.IndexOf(args, key) + 1)
            is { } value && Array.IndexOf(args, key) >= 0 ? value : throw new ArgumentException("Missing " + key);
        string input = Path.GetFullPath(Argument("--input"));
        var model = Enum.Parse<SuperResolutionModel>(Argument("--model"));
        string output = Path.Combine(workDir, "output.mp4");
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        using var http = new HttpClient();
        var downloads = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
        var sr = new VideoSuperResolutionService(downloads, NullLogger<VideoSuperResolutionService>.Instance,
            new AppPreferences { SuperResolutionTileSizeIndex = 2 });
        // Keep downloads/validation outside the measured interval, as with an installed model.
        sr.EnsureModelAsync(model).GetAwaiter().GetResult();
        using var phases = new StreamWriter(Path.Combine(workDir, "phases.jsonl"), false) { AutoFlush = true };
        void Phase(string phase) => phases.WriteLine(JsonSerializer.Serialize(new { phase, utc = DateTimeOffset.UtcNow }));
        var measured = new MeasuredSrService(sr, Phase);
        var service = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance, measured);
        var source = service.ProbeAsync(input).GetAwaiter().GetResult() ?? throw new InvalidOperationException("Cannot probe input video");
        var options = new VideoTranscodeOptions
        {
            SuperResolutionEnabled = true, SuperResolutionModel = model, SuperResolutionScale = 2,
            SuperResolutionBackend = VideoSrBackend.Gpu, SuperResolutionAllowCpuFallback = false,
            HardwareBackend = HardwareBackend.Nvidia, VideoCodec = VideoCodec.H264,
            AudioCodec = AudioCodec.None, RateControl = RateControlMode.Crf, Crf = 18,
            Preset = PresetLevel.Fast, HdrOutput = VideoHdrOutput.ForceSdr
        };
        int gen2 = GC.CollectionCount(2);
        long allocated = GC.GetTotalAllocatedBytes();
        var startUtc = DateTimeOffset.UtcNow;
        Phase("transcode-start");
        var clock = Stopwatch.StartNew();
        service.TranscodeAsync(input, output, options).GetAwaiter().GetResult();
        double elapsedSeconds = clock.Elapsed.TotalSeconds;
        var endUtc = DateTimeOffset.UtcNow;
        Phase("transcode-done");
        var result = service.ProbeAsync(output).GetAwaiter().GetResult()!;
        int outputFrames = ReadVideoTimes(output).Count;
        if (result.Width != source.Width * 2 || result.Height != source.Height * 2 || outputFrames != measured.Frames)
            throw new InvalidOperationException("Output dimensions or temporal frame count do not match the input");
        var record = new
        {
            model = model.ToString(), input, source.Width, source.Height, source.Fps, source.DurationMs,
            output, outputWidth = result.Width, outputHeight = result.Height, outputFrames,
            device = measured.Device, tileWindow = model == SuperResolutionModel.NanoVsr644K ? 192 : 384,
            temporalFrames = model == SuperResolutionModel.NanoVsr644K ? 5 : 1,
            encoder = "h264_nvenc", preset = "p4", constQp = 18, audio = false,
            startUtc, endUtc, elapsedSeconds, endToEndFps = outputFrames / elapsedSeconds,
            sessionSetupMs = measured.SetupMs, inferenceMs = measured.InferenceMs,
            firstInferenceMs = measured.FirstMs, firstInferenceFrames = measured.FirstFrames,
            warmInferenceFps = 1000d * (measured.Frames - measured.FirstFrames) / (measured.InferenceMs - measured.FirstMs),
            steadyStartUtc = measured.SteadyStartUtc, lastInferenceUtc = measured.LastInferenceUtc,
            allocatedBytes = GC.GetTotalAllocatedBytes() - allocated, gen2Collections = GC.CollectionCount(2) - gen2,
            peakWorkingSetMiB = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d
        };
        string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(workDir, "result.json"), json);
        Console.WriteLine(json);
        return 0;
    }

    private sealed class MeasuredSrService(IVideoSuperResolutionService inner, Action<string> phase) : IVideoSuperResolutionService
    {
        internal double SetupMs, InferenceMs, FirstMs;
        internal int Frames, FirstFrames;
        internal string? Device;
        internal DateTimeOffset SteadyStartUtc, LastInferenceUtc;
        public event Action<ModelDownloadProgress>? DownloadProgressChanged { add => inner.DownloadProgressChanged += value; remove => inner.DownloadProgressChanged -= value; }
        public event Action<VideoSrDevice>? DeviceChanged { add => inner.DeviceChanged += value; remove => inner.DeviceChanged -= value; }
        public bool IsModelAvailable(SuperResolutionModel model) => inner.IsModelAvailable(model);
        public Task EnsureModelAsync(SuperResolutionModel model, CancellationToken ct = default) => inner.EnsureModelAsync(model, ct);
        public Task InstallAccelerationAsync(CancellationToken ct = default) => inner.InstallAccelerationAsync(ct);
        public async Task<IVideoFrameUpscaler> CreateSessionAsync(VideoTranscodeOptions options, CancellationToken ct = default)
        {
            var timer = Stopwatch.StartNew();
            var session = await inner.CreateSessionAsync(options, ct).ConfigureAwait(false);
            SetupMs += timer.Elapsed.TotalMilliseconds;
            Device = session.Device.Name;
            phase("session-ready");
            return session is IVideoFrameSequenceUpscaler sequence ? new MeasuredSequence(sequence, this) : new MeasuredFrame(session, this);
        }
        internal T Measure<T>(int count, Func<T> run)
        {
            bool first = Frames == 0;
            if (first) phase("first-inference-start");
            long start = Stopwatch.GetTimestamp();
            T result = run();
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            InferenceMs += elapsed;
            Frames += count;
            LastInferenceUtc = DateTimeOffset.UtcNow;
            if (first)
            {
                FirstMs = elapsed; FirstFrames = count; SteadyStartUtc = LastInferenceUtc;
                phase("steady-start");
            }
            return result;
        }
    }

    private sealed class MeasuredFrame(IVideoFrameUpscaler inner, MeasuredSrService stats) : IVideoFrameUpscaler
    {
        public VideoSrDevice Device => inner.Device;
        public SKBitmap Upscale(SKBitmap source, int scale, CancellationToken ct) => stats.Measure(1, () => inner.Upscale(source, scale, ct));
        public void Dispose() => inner.Dispose();
    }
    private sealed class MeasuredSequence(IVideoFrameSequenceUpscaler inner, MeasuredSrService stats) : IVideoFrameSequenceUpscaler
    {
        public VideoSrDevice Device => inner.Device;
        public int TemporalWindow => inner.TemporalWindow;
        public SKBitmap Upscale(SKBitmap source, int scale, CancellationToken ct) => stats.Measure(1, () => inner.Upscale(source, scale, ct));
        public SKBitmap[] UpscaleSequence(IReadOnlyList<SKBitmap> source, int scale, CancellationToken ct)
            => stats.Measure(source.Count, () => inner.UpscaleSequence(source, scale, ct));
        public void Dispose() => inner.Dispose();
    }
}
