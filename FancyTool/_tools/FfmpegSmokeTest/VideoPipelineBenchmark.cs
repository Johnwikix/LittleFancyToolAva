using System.Diagnostics;
using System.Text.Json;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging.Abstractions;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunVideoPipelineBenchmark(string workDir, string[] args)
    {
        string Arg(string key, string fallback) => Array.IndexOf(args, key) is var index && index >= 0 ? args[index + 1] : fallback;
        int width = int.Parse(Arg("--width", "640")), height = int.Parse(Arg("--height", "360"));
        int frames = int.Parse(Arg("--frames", "48")), repeats = int.Parse(Arg("--repeats", "3"));
        string input = Arg("--input", Path.Combine(workDir, "pipeline-source.avi"));
        if (!args.Contains("--input"))
        {
            WriteTestAvi(input, width, height, 24, frames / 24d);
            if (args.Contains("--repeat-source"))
                FillAviFrames(input, (pixels, index) => pixels.Fill((index / 24 % 3) switch { 0 => (byte)30, 1 => (byte)180, _ => (byte)60 }));
        }
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        using var http = new HttpClient();
        var downloads = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
        using var sr = new VideoSuperResolutionService(downloads, NullLogger<VideoSuperResolutionService>.Instance,
            new AppPreferences { SuperResolutionTileSizeIndex = int.Parse(Arg("--tile-index", "2")) });
        var interpolation = new VideoInterpolationService(downloads, NullLogger<VideoInterpolationService>.Instance);
        using var interpolationLifetime = TestServiceLifetime(interpolation);
        VideoSrDevice? srDevice = null, rifeDevice = null;
        sr.DeviceChanged += d => { srDevice = d; Console.WriteLine("SR DEVICE " + d + "; pixels=" + d.UsesGpuPixels); };
        interpolation.DeviceChanged += d => { rifeDevice = d; Console.WriteLine("RIFE DEVICE " + d + "; pixels=" + d.UsesGpuPixels); };
        var service = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance, sr, interpolation);
        var records = new List<object>();
        string[] modes = Arg("--modes", "sr,rife,both,both-reverse").Split(',');
        foreach (string mode in modes)
        for (int iteration = 0; iteration <= repeats; iteration++)
        {
            string label = $"{mode}-{iteration}";
            Console.WriteLine($"START {label} {DateTimeOffset.UtcNow:O}");
            string profile = Path.GetFullPath(Path.Combine(workDir, label));
            Directory.CreateDirectory(profile);
            Environment.SetEnvironmentVariable("FANCYTOOL_VIDEO_PROFILE", profile);
            var options = new VideoTranscodeOptions
            {
                SuperResolutionEnabled = mode is not ("rife" or "plain"), SuperResolutionModel = Enum.Parse<VideoSrModel>(Arg("--model", "FsrcnnxM")),
                SuperResolutionUseFp16 = !args.Contains("--fp32"), SuperResolutionScale = int.Parse(Arg("--sr-scale", "2")), SuperResolutionBackend = VideoSrBackend.Gpu,
                InterpolationMode = mode is "sr" or "plain" ? VideoInterpolationMode.None : Enum.Parse<VideoInterpolationMode>(Arg("--interpolation-mode", "Rife")),
                InterpolationModel = Enum.Parse<VideoInterpolationModel>(Arg("--interpolation-model", "Rife425Lite")),
                InterpolationBackend = VideoSrBackend.Gpu, InterpolationMultiplier = int.Parse(Arg("--multiplier", "2")),
                FrameProcessingOrder = mode == "both-reverse" ? VideoFrameProcessingOrder.UpscaleThenInterpolation : VideoFrameProcessingOrder.InterpolationThenUpscale,
                InterpolationSceneDetection = args.Contains("--scene-detection"), AudioCodec = AudioCodec.Aac,
                HardwareBackend = HardwareBackend.Nvidia, VideoCodec = VideoCodec.H264, Preset = PresetLevel.Fast,
                HdrOutput = VideoHdrOutput.ForceSdr, Crf = 18, Profile = Arg("--profile", "")
            };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            string output = Path.Combine(profile, "output.mp4");
            long allocated = GC.GetTotalAllocatedBytes();
            var startUtc = DateTimeOffset.UtcNow;
            var timer = Stopwatch.StartNew();
            service.TranscodeAsync(input, output, options, ct: deadline.Token).GetAwaiter().GetResult();
            double elapsedMs = timer.Elapsed.TotalMilliseconds;
            var endUtc = DateTimeOffset.UtcNow;
            long allocations = GC.GetTotalAllocatedBytes() - allocated;
            int nativeSrSessions = 0;
            if (options.SuperResolutionEnabled)
            {
                using var lease = sr.CreateSessionAsync(options).GetAwaiter().GetResult();
                nativeSrSessions = (lease as IVideoInferenceSessions)?.Count ?? 1;
            }
            var probe = service.ProbeAsync(output).GetAwaiter().GetResult()!;
            int rifeParallelism = options.InterpolationMode == VideoInterpolationMode.Rife ? 1 : 0;
            var times = ReadVideoTimes(output);
            int expected = options.InterpolationMode == VideoInterpolationMode.None ? frames : frames * options.InterpolationMultiplier;
            int expectedScale = options.SuperResolutionEnabled ? options.SuperResolutionScale : 1;
            if (times.Count != expected || !probe.HasAudio || probe.Width != width * expectedScale || probe.Height != height * expectedScale
                || times.Zip(times.Skip(1)).Any(t => t.Second <= t.First))
                throw new InvalidOperationException($"Invalid output: {times.Count} frames, expected {expected}, audio={probe.HasAudio}");
            var record = new { mode, iteration, repeatSource = args.Contains("--repeat-source"), startType = iteration == 0 ? "first" : "repeat", width, height,
                model = options.SuperResolutionModel.ToString(), options.SuperResolutionUseFp16, options.SuperResolutionScale,
                interpolation = options.InterpolationMode.ToString(), interpolationModel = options.InterpolationModel.ToString(), options.InterpolationMultiplier,
                nativeSrSessions, rifeParallelism,
                srDevice = mode != "rife" ? srDevice : null, rifeDevice = mode != "sr" ? rifeDevice : null,
                srGpuPixels = mode != "rife" && srDevice?.UsesGpuPixels == true, rifeGpuPixels = mode != "sr" && rifeDevice?.UsesGpuPixels == true,
                srHostMemory = options.SuperResolutionEnabled && srDevice?.UsesGpuHostMemory == true,
                rifeHostMemory = options.InterpolationMode == VideoInterpolationMode.Rife && rifeDevice?.UsesGpuHostMemory == true,
                elapsedMs, outputFrames = times.Count, outputFps = times.Count * 1000 / elapsedMs,
                allocatedBytes = allocations, outputWidth = probe.Width, outputHeight = probe.Height, probe.DurationMs, probe.PixelFormat, profile,
                startUtc, endUtc, peakWorkingSetMiB = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d };
            records.Add(record);
            Console.WriteLine(JsonSerializer.Serialize(record));
            File.WriteAllText(Path.Combine(workDir, "measurements.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        }
        Environment.SetEnvironmentVariable("FANCYTOOL_VIDEO_PROFILE", null);
        return 0;
    }
}
