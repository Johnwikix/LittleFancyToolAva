using System.Runtime.InteropServices;
using FancyToolAva.Models;
using FancyToolAva.Services;
using SkiaSharp;

namespace FancyToolAva.Cli.Commands;

internal static class VideoCommands
{
    public static void Register(Dictionary<string, Func<CliContext, Task<int>>> map)
    {
        map["video probe"] = ProbeAsync;
        map["video preview"] = PreviewAsync;
        map["video transcode"] = ctx => TranscodeAsync(ctx, concat: false);
        map["video concat"] = ctx => TranscodeAsync(ctx, concat: true);
    }

    private static async Task<int> ProbeAsync(CliContext ctx)
    {
        string input = ctx.Args.Require("--input", ctx.Zh ? "需要 -i/--input。" : "-i/--input is required.");
        if (!File.Exists(input)) throw new FileNotFoundException(input);
        var service = ctx.Get<IVideoTranscodeService>();
        VideoProbeInfo? probe = await service.ProbeAsync(input, ctx.CancellationToken).ConfigureAwait(false);
        if (probe is null) throw new InvalidOperationException("No media streams were found.");
        if (ctx.Json)
        {
            ctx.WriteJson(new
            {
                filePath = probe.FilePath,
                durationMs = probe.DurationMs,
                width = probe.Width,
                height = probe.Height,
                fps = probe.Fps,
                videoCodec = probe.VideoCodec,
                audioCodec = probe.AudioCodec,
                container = probe.Container,
                bitRate = probe.BitRate,
                hasVideo = probe.HasVideo,
                hasAudio = probe.HasAudio,
                frameCount = probe.FrameCount,
                pixelFormat = probe.PixelFormat,
                bitDepth = probe.BitDepth,
                colorPrimaries = probe.ColorPrimaries,
                colorTransfer = probe.ColorTransfer,
                colorSpace = probe.ColorSpace,
                colorRange = probe.ColorRange,
                isHdr = probe.IsHdr,
            });
        }
        else
        {
            ctx.WriteResult(probe.Display);
        }
        return 0;
    }

    private static async Task<int> PreviewAsync(CliContext ctx)
    {
        string input = ctx.Args.Require("--input", ctx.Zh ? "需要 -i/--input。" : "-i/--input is required.");
        string output = ctx.Args.Require("--out", ctx.Zh ? "需要 -o/--out。" : "-o/--out is required.");
        double at = ctx.Args.GetDouble("--at", 0);
        if (!File.Exists(input)) throw new FileNotFoundException(input);
        var service = ctx.Get<IVideoTranscodeService>();
        VideoPreviewFrame? frame = await service.GetPreviewAsync(input, at, ctx.CancellationToken).ConfigureAwait(false);
        if (frame is null) throw new InvalidOperationException("No frame is available at the requested position.");
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        Marshal.Copy(frame.Pixels, 0, bitmap.GetPixels(), frame.Pixels.Length);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(output, data.ToArray(), ctx.CancellationToken).ConfigureAwait(false);
        if (ctx.Json) ctx.WriteJson(new { input, output, width = frame.Width, height = frame.Height, positionSeconds = frame.PositionSeconds });
        else ctx.WriteResult(output);
        return 0;
    }

    private static async Task<int> TranscodeAsync(CliContext ctx, bool concat)
    {
        string? output = ctx.Args.Get("--out");
        string? outputDirectory = ctx.Args.Get("--out-dir");
        if (concat) output = ctx.Args.Require("--out", ctx.Zh ? "需要 -o/--out。" : "-o/--out is required.");
        if (!concat)
        {
            IReadOnlyList<string> inputs = ctx.Args.GetAll("--input");
            if (inputs.Count == 0) throw new CliUsageException(ctx.Zh ? "需要 -i/--input。" : "-i/--input is required.");
            if (inputs.Count > 1 && outputDirectory is null)
                throw new CliUsageException(ctx.Zh ? "批量处理需要 --out-dir。" : "Batch processing requires --out-dir.");
            if (inputs.Count > 1 && output is not null)
                throw new CliUsageException(ctx.Zh ? "多个输入不能同时使用 -o/--out。" : "-o/--out cannot be used with multiple inputs.");
            if (inputs.Count == 1 && output is null && outputDirectory is null)
                throw new CliUsageException(ctx.Zh ? "需要 -o/--out 或 --out-dir。" : "-o/--out or --out-dir is required.");

            var service = ctx.Get<IVideoTranscodeService>();
            var results = new List<object>();
            int failed = 0;
            foreach (string input in inputs)
            {
                if (!File.Exists(input))
                {
                    failed++;
                    ctx.Error((ctx.Zh ? "找不到文件: " : "File not found: ") + input);
                    continue;
                }
                string target;
                if (output is not null)
                {
                    target = output;
                    if (Path.GetDirectoryName(target) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
                }
                else
                {
                    VideoContainer container = ctx.Args.Get("--container") is not null
                        ? ParseContainer(ctx, "out.mp4")
                        : VideoContainer.Mp4;
                    Directory.CreateDirectory(outputDirectory!);
                    target = UniquePath(Path.Combine(outputDirectory!, Path.GetFileNameWithoutExtension(input) + ContainerExtension(container)));
                }
                if (output is not null && File.Exists(target) && !ctx.Args.GetBool("--force"))
                {
                    failed++;
                    ctx.Error((ctx.Zh ? "输出文件已存在，使用 --force 覆盖: " : "Output exists; use --force to overwrite: ") + target);
                    continue;
                }
                VideoTranscodeOptions fileOptions = BuildOptions(ctx, target, concat: false);
                try
                {
                    var progress = ctx.Quiet ? null : new Progress<double>(p => ctx.Progress(p));
                    await service.TranscodeAsync(input, target, fileOptions, progress, ctx.CancellationToken).ConfigureAwait(false);
                    ctx.ProgressDone();
                    results.Add(new { input, output = target, success = true, error = (string?)null });
                    if (!ctx.Json) ctx.WriteResult(target);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    ctx.ProgressDone();
                    ctx.Error((ctx.Zh ? "转码失败: " : "Transcode failed: ") + input + " - " + ex.Message);
                    results.Add(new { input, output = target, success = false, error = ex.Message });
                }
            }
            if (ctx.Json && inputs.Count > 1) ctx.WriteJson(new { failed, results });
            else if (ctx.Json && results.Count == 1) ctx.WriteJson(results[0]);
            return failed == 0 ? 0 : 1;
        }

        if (File.Exists(output!) && !ctx.Args.GetBool("--force"))
            throw new CliUsageException((ctx.Zh ? "输出文件已存在，使用 --force 覆盖: " : "Output exists; use --force to overwrite: ") + output);
        if (Path.GetDirectoryName(output) is { Length: > 0 } concatParent) Directory.CreateDirectory(concatParent);
        VideoTranscodeOptions options = BuildOptions(ctx, output!, concat: true);
        var concatService = ctx.Get<IVideoTranscodeService>();
        var concatProgress = ctx.Quiet ? null : new Progress<double>(p => ctx.Progress(p));
        List<VideoConcatSegment> segments = ParseSegments(ctx);
        if (segments.Count == 0)
            throw new CliUsageException(ctx.Zh ? "至少需要一个 --segment。" : "At least one --segment is required.");
        await concatService.ConcatTranscodeAsync(segments, output!, options, concatProgress, ctx.CancellationToken).ConfigureAwait(false);
        ctx.ProgressDone();
        if (ctx.Json)
            ctx.WriteJson(new { output, segments = segments.Select(s => new { s.FilePath, s.LoopCount, s.TrimStartSeconds, s.TrimEndSeconds }) });
        else
            ctx.WriteResult(output);
        return 0;
    }

    private static string ContainerExtension(VideoContainer container) => container switch
    {
        VideoContainer.Mkv => ".mkv",
        VideoContainer.Mov => ".mov",
        VideoContainer.Avi => ".avi",
        VideoContainer.Gif => ".gif",
        _ => ".mp4",
    };

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string directory = Path.GetDirectoryName(path) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(directory, $"{stem}_{i}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static VideoTranscodeOptions BuildOptions(CliContext ctx, string output, bool concat)
    {
        VideoContainer container = ParseContainer(ctx, output);
        VideoCodec videoCodec = ParseVideoCodec(ctx, container);
        AudioCodec audioCodec = ParseAudioCodec(ctx, container);
        var options = new VideoTranscodeOptions
        {
            Container = container,
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            RateControl = ctx.Args.GetChoice("--rate-control", "crf", "crf", "bitrate") == "bitrate"
                ? RateControlMode.Bitrate : RateControlMode.Crf,
            Crf = ctx.Args.GetInt("--crf", 23),
            VideoBitrateKbps = ctx.Args.GetInt("--video-bitrate", 2500),
            TwoPassEnabled = ctx.Args.GetBool("--two-pass"),
            AudioBitrateKbps = ctx.Args.GetInt("--audio-bitrate", 128),
            Preset = ParsePreset(ctx),
            HardwareBackend = ctx.Args.GetChoice("--hardware", "software", "software", "nvidia", "intel", "amd") switch
            {
                "nvidia" => HardwareBackend.Nvidia,
                "intel" => HardwareBackend.Intel,
                "amd" => HardwareBackend.Amd,
                _ => HardwareBackend.Software,
            },
            Profile = ctx.Args.Get("--profile") ?? string.Empty,
            Level = ctx.Args.Get("--level") ?? string.Empty,
            AudioGainDb = ctx.Args.GetDouble("--audio-gain", 0),
            ScaleMode = ctx.Args.GetChoice("--scale-mode", "none", "none", "fit", "exact", "width", "height") switch
            {
                "fit" => ScaleMode.FitWithin,
                "exact" => ScaleMode.Exact,
                "width" => ScaleMode.Width,
                "height" => ScaleMode.Height,
                _ => ScaleMode.None,
            },
            ScaleWidth = ctx.Args.GetInt("--scale-width", 1920),
            ScaleHeight = ctx.Args.GetInt("--scale-height", 1080),
            KeepAspect = !ctx.Args.GetBool("--no-keep-aspect"),
            FpsMode = ctx.Args.GetChoice("--fps-mode", "source", "source", "fixed", "peak") switch
            {
                "fixed" => FpsMode.Fixed,
                "peak" => FpsMode.Peak,
                _ => FpsMode.SameAsSource,
            },
            FpsValue = ctx.Args.GetDouble("--fps", 30),
            Deinterlace = ctx.Args.GetChoice("--deinterlace", "none", "none", "yadif", "bwdif") switch
            {
                "yadif" => DeinterlaceMode.Yadif,
                "bwdif" => DeinterlaceMode.Bwdif,
                _ => DeinterlaceMode.None,
            },
            Denoise = ctx.Args.GetChoice("--denoise", "none", "none", "light", "medium", "strong") switch
            {
                "light" => DenoiseMode.Hqdn3dLight,
                "medium" => DenoiseMode.Hqdn3dMedium,
                "strong" => DenoiseMode.Hqdn3dStrong,
                _ => DenoiseMode.None,
            },
            GifWidth = ctx.Args.GetInt("--gif-width", 480),
            GifFps = ctx.Args.GetInt("--gif-fps", 15),
            GifMaxColors = ctx.Args.GetInt("--gif-colors", 256),
            GifDither = ctx.Args.GetChoice("--gif-dither", "bayer", "none", "bayer", "floyd", "sierpinski") switch
            {
                "none" => GifDither.None,
                "floyd" => GifDither.FloydSteinberg,
                "sierpinski" => GifDither.Sierpinski,
                _ => GifDither.Bayer,
            },
            GifLoop = ctx.Args.GetInt("--gif-loop", 0),
            GifStatsMode = ctx.Args.GetChoice("--gif-stats", "diff", "diff", "single")!,
            TrimStartSeconds = ctx.Args.GetDouble("--trim-start", 0),
            TrimEndSeconds = ctx.Args.GetNullableDouble("--trim-end"),
            HdrOutput = ctx.Args.GetChoice("--hdr", "same", "same", "hdr", "sdr") switch
            {
                "hdr" => VideoHdrOutput.ForceHdr,
                "sdr" => VideoHdrOutput.ForceSdr,
                _ => VideoHdrOutput.SameAsSource,
            },
        };

        string? crop = ctx.Args.Get("--crop");
        if (!string.IsNullOrWhiteSpace(crop))
        {
            string[] parts = crop.Split(',');
            if (parts.Length != 4 || !parts.All(p => int.TryParse(p, out _)))
                throw new CliUsageException("--crop must be top,bottom,left,right");
            options.CropEnabled = true;
            options.CropTop = int.Parse(parts[0]);
            options.CropBottom = int.Parse(parts[1]);
            options.CropLeft = int.Parse(parts[2]);
            options.CropRight = int.Parse(parts[3]);
        }

        if (ctx.Args.GetBool("--sr"))
        {
            options.SuperResolutionEnabled = true;
            options.SuperResolutionModel = ParseVideoSrModel(ctx);
            options.SuperResolutionScale = ctx.Args.GetInt("--sr-scale", 2);
            if (options.SuperResolutionScale is not (2 or 4)) throw new CliUsageException("--sr-scale must be 2 or 4.");
            options.SuperResolutionBackend = ParseVideoBackend(ctx, "--sr-backend", allowDirectMl: true);
            options.SuperResolutionUseFp16 = ctx.Args.GetBool("--sr-fp16");
        }

        string interpolation = ctx.Args.GetChoice("--interpolate", "none", "none", "rife", "motion", "amd", "nvidia")!;
        options.InterpolationMode = interpolation switch
        {
            "rife" => VideoInterpolationMode.Rife,
            "motion" => VideoInterpolationMode.MotionCompensation,
            "amd" => VideoInterpolationMode.AmdOpticalFlow,
            "nvidia" => VideoInterpolationMode.NvidiaOpticalFlow,
            _ => VideoInterpolationMode.None,
        };
        options.InterpolationModel = ctx.Args.GetChoice("--interpolation-model", "rife425-lite", "rife425-lite", "rife425") switch
        {
            "rife425" => VideoInterpolationModel.Rife425,
            _ => VideoInterpolationModel.Rife425Lite,
        };
        options.InterpolationMultiplier = ctx.Args.GetInt("--interpolation-multiplier", 2);
        if (options.InterpolationMultiplier is not (2 or 4 or 6))
            throw new CliUsageException("--interpolation-multiplier must be 2, 4, or 6.");
        options.InterpolationBackend = ParseVideoBackend(ctx, "--interpolation-backend", allowDirectMl: false);
        options.InterpolationAllowCpuFallback = ctx.Args.GetBool("--interpolation-cpu-fallback");
        options.InterpolationSceneDetection = !ctx.Args.GetBool("--no-scene-detection");
        options.FrameProcessingOrder = ctx.Args.GetChoice("--processing-order", "auto", "auto", "interpolate-first", "upscale-first") switch
        {
            "interpolate-first" => VideoFrameProcessingOrder.InterpolationThenUpscale,
            "upscale-first" => VideoFrameProcessingOrder.UpscaleThenInterpolation,
            _ => VideoFrameProcessingOrder.Auto,
        };
        return options;
    }

    private static List<VideoConcatSegment> ParseSegments(CliContext ctx)
    {
        var segments = new List<VideoConcatSegment>();
        int current = -1;
        foreach (KeyValuePair<string, string?> pair in ctx.Args.Ordered)
        {
            if (pair.Key == "--segment" && pair.Value is { } value)
            {
                string[] parts = value.Split(';');
                string file = parts[0];
                int loops = parts.Length > 1 && int.TryParse(parts[1], out int parsedLoops) ? parsedLoops : 1;
                double start = parts.Length > 2 && double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsedStart) ? parsedStart : 0;
                double? end = parts.Length > 3 && double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsedEnd) ? parsedEnd : null;
                if (!File.Exists(file)) throw new FileNotFoundException(file);
                segments.Add(new VideoConcatSegment(file, Math.Clamp(loops, 1, 99), start, end));
                current = segments.Count - 1;
            }
            else if (current >= 0 && pair.Key is "--loop-count" or "--trim-start" or "--trim-end" && pair.Value is { } raw)
            {
                VideoConcatSegment segment = segments[current];
                segments[current] = pair.Key switch
                {
                    "--loop-count" when int.TryParse(raw, out int loops) => segment with { LoopCount = Math.Clamp(loops, 1, 99) },
                    "--trim-start" when double.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double start) => segment with { TrimStartSeconds = start },
                    "--trim-end" when double.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double end) => segment with { TrimEndSeconds = end },
                    _ => segment,
                };
            }
        }
        return segments;
    }

    private static VideoContainer ParseContainer(CliContext ctx, string output)
    {
        string? value = ctx.Args.Get("--container");
        string container = value ?? Path.GetExtension(output).TrimStart('.');
        return container.ToLowerInvariant() switch
        {
            "mp4" or "m4v" => VideoContainer.Mp4,
            "mkv" => VideoContainer.Mkv,
            "mov" => VideoContainer.Mov,
            "avi" => VideoContainer.Avi,
            "gif" => VideoContainer.Gif,
            _ => throw new CliUsageException($"Cannot infer container from output extension: {container}"),
        };
    }

    private static VideoCodec ParseVideoCodec(CliContext ctx, VideoContainer container)
    {
        string fallback = container == VideoContainer.Gif ? "gif" : "h264";
        return ctx.Args.GetChoice("--video-codec", fallback,
            "h264", "h265", "av1-aom", "av1-svt", "gif", "vp8", "vp9", "mpeg4", "copy") switch
        {
            "h265" => VideoCodec.H265,
            "av1-aom" => VideoCodec.Av1Aom,
            "av1-svt" => VideoCodec.Av1Svt,
            "gif" => VideoCodec.Gif,
            "vp8" => VideoCodec.Vp8,
            "vp9" => VideoCodec.Vp9,
            "mpeg4" => VideoCodec.Mpeg4,
            "copy" => VideoCodec.Copy,
            _ => VideoCodec.H264,
        };
    }

    private static AudioCodec ParseAudioCodec(CliContext ctx, VideoContainer container)
    {
        string fallback = container == VideoContainer.Gif ? "none" : "aac";
        return ctx.Args.GetChoice("--audio-codec", fallback,
            "aac", "mp3", "opus", "vorbis", "flac", "ac3", "none", "copy") switch
        {
            "mp3" => AudioCodec.Mp3,
            "opus" => AudioCodec.Opus,
            "vorbis" => AudioCodec.Vorbis,
            "flac" => AudioCodec.Flac,
            "ac3" => AudioCodec.Ac3,
            "none" => AudioCodec.None,
            "copy" => AudioCodec.Copy,
            _ => AudioCodec.Aac,
        };
    }

    private static PresetLevel ParsePreset(CliContext ctx) =>
        ctx.Args.GetChoice("--preset", "medium",
            "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo") switch
        {
            "ultrafast" => PresetLevel.Ultrafast,
            "superfast" => PresetLevel.Superfast,
            "veryfast" => PresetLevel.Veryfast,
            "faster" => PresetLevel.Faster,
            "fast" => PresetLevel.Fast,
            "slow" => PresetLevel.Slow,
            "slower" => PresetLevel.Slower,
            "veryslow" => PresetLevel.Veryslow,
            "placebo" => PresetLevel.Placebo,
            _ => PresetLevel.Medium,
        };

    private static VideoSrModel ParseVideoSrModel(CliContext ctx) =>
        ctx.Args.GetChoice("--sr-model", "anime4k-cnn-s",
            "anime4k-cnn-s", "anime4k-cnn-m", "fsrcnnx-s", "fsrcnnx-m", "ravu-lite-r2", "ravu-lite-r3") switch
        {
            "anime4k-cnn-m" => VideoSrModel.Anime4KCnnM,
            "fsrcnnx-s" => VideoSrModel.FsrcnnxS,
            "fsrcnnx-m" => VideoSrModel.FsrcnnxM,
            "ravu-lite-r2" => VideoSrModel.RavuLiteR2,
            "ravu-lite-r3" => VideoSrModel.RavuLiteR3,
            _ => VideoSrModel.Anime4KCnnS,
        };

    private static VideoSrBackend ParseVideoBackend(CliContext ctx, string option, bool allowDirectMl)
    {
        string value = ctx.Args.GetChoice(option, "auto", "auto", "npu", "gpu", "cpu", "directml", "vendor")!;
        if (!allowDirectMl && value is "directml" or "vendor")
            throw new CliUsageException($"{option} does not support {value}.");
        return value switch
        {
            "npu" => VideoSrBackend.PreferNpu,
            "gpu" => VideoSrBackend.Gpu,
            "cpu" => VideoSrBackend.Cpu,
            "directml" => VideoSrBackend.DirectMl,
            "vendor" => VideoSrBackend.VendorGpu,
            _ => VideoSrBackend.Auto,
        };
    }
}