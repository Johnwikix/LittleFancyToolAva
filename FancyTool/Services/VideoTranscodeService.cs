using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FancyToolAva.Models;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

public sealed class VideoTranscodeService : IVideoTranscodeService
{
    private readonly ILogger<VideoTranscodeService> _logger;
    private readonly IFfmpegService _ffmpeg;

    public VideoTranscodeService(ILogger<VideoTranscodeService> logger, IFfmpegService ffmpeg)
    {
        _logger = logger;
        _ffmpeg = ffmpeg;
    }

    private string ResolveFfmpegExe()
    {
        string? dir = _ffmpeg.ResolvedDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !_ffmpeg.IsAvailable)
            throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegNotAvailable"));

        string exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        if (!File.Exists(exe))
            throw new FileNotFoundException(LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegExeNotFound", exe), exe);
        return exe;
    }

    private string ResolveFfprobeExe()
    {
        string? dir = _ffmpeg.ResolvedDirectory;
        if (string.IsNullOrWhiteSpace(dir)) throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegNotAvailable"));
        string exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (File.Exists(exe)) return exe;
        // fallback to ffmpeg for probe parsing
        return ResolveFfmpegExe();
    }

    public async Task<VideoProbeInfo?> ProbeAsync(string inputPath, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException(inputPath);
        string ffprobe = ResolveFfprobeExe();
        bool isFfprobe = Path.GetFileNameWithoutExtension(ffprobe).Equals("ffprobe", StringComparison.OrdinalIgnoreCase);

        if (isFfprobe)
        {
            return await ProbeWithFfprobeAsync(ffprobe, inputPath, ct).ConfigureAwait(false);
        }
        else
        {
            return await ProbeWithFfmpegAsync(ffprobe, inputPath, ct).ConfigureAwait(false);
        }
    }

    private async Task<VideoProbeInfo?> ProbeWithFfprobeAsync(string ffprobe, string input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            Arguments = $"-v error -show_format -show_streams -of json \"{input}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        AppendFfmpegEnv(psi);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        string output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        string err = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            _logger.LogWarning("ffprobe failed {Input}: {Err}", input, err);
            throw new InvalidOperationException(err);
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            var streams = root.GetProperty("streams");
            var format = root.GetProperty("format");

            long durationMs = 0;
            if (format.TryGetProperty("duration", out var durEl) && double.TryParse(durEl.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double durSec))
                durationMs = (long)(durSec * 1000);
            long bitRate = 0;
            if (format.TryGetProperty("bit_rate", out var brEl) && long.TryParse(brEl.GetString(), out long br)) bitRate = br;
            string container = format.TryGetProperty("format_name", out var fmtEl) ? fmtEl.GetString() ?? "" : "";

            int width = 0, height = 0;
            double fps = 0;
            string vCodec = "", aCodec = "";
            bool hasVideo = false, hasAudio = false;

            foreach (var s in streams.EnumerateArray())
            {
                string codecType = s.TryGetProperty("codec_type", out var ctEl) ? ctEl.GetString() ?? "" : "";
                string codecName = s.TryGetProperty("codec_name", out var cnEl) ? cnEl.GetString() ?? "" : "";
                if (codecType == "video" && !hasVideo)
                {
                    hasVideo = true;
                    vCodec = codecName;
                    if (s.TryGetProperty("width", out var wEl)) width = wEl.GetInt32();
                    if (s.TryGetProperty("height", out var hEl)) height = hEl.GetInt32();
                    string fpsStr = "";
                    if (s.TryGetProperty("avg_frame_rate", out var fpsEl)) fpsStr = fpsEl.GetString() ?? "";
                    if (string.IsNullOrEmpty(fpsStr) || fpsStr == "0/0")
                        if (s.TryGetProperty("r_frame_rate", out var rEl)) fpsStr = rEl.GetString() ?? "";
                    fps = ParseFrameRate(fpsStr);
                }
                else if (codecType == "audio" && !hasAudio)
                {
                    hasAudio = true;
                    aCodec = codecName;
                }
            }

            return new VideoProbeInfo(input, durationMs, width, height, fps, vCodec, aCodec, container, bitRate, hasAudio, hasVideo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse ffprobe json for {Input}", input);
            throw;
        }
    }

    private async Task<VideoProbeInfo?> ProbeWithFfmpegAsync(string ffmpeg, string input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-hide_banner -i \"{input}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        AppendFfmpegEnv(psi);
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        string err = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        // ffmpeg -i always exits non-zero; parse err for Duration/format
        return ParseFfmpegProbe(input, err);
    }

    private static VideoProbeInfo? ParseFfmpegProbe(string input, string text)
    {
        long durationMs = 0;
        string container = "";
        int width = 0, height = 0;
        double fps = 0;
        string vCodec = "", aCodec = "";
        bool hasVideo = false, hasAudio = false;

        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Duration:", StringComparison.Ordinal))
            {
                // Duration: 00:00:12.34, start: 0.000000, bitrate: 1234 kb/s
                var parts = t.Split(',');
                var durPart = parts[0].Replace("Duration:", "").Trim();
                if (TimeSpan.TryParse(durPart, out var ts)) durationMs = (long)ts.TotalMilliseconds;
            }
            if (t.Contains("Stream #") && t.Contains("Video:"))
            {
                hasVideo = true;
                // extract codec and resolution fps
                // e.g. Video: h264 (High) ... 1920x1080 [SAR ...], 30 fps
                var idx = t.IndexOf("Video:", StringComparison.Ordinal);
                if (idx >= 0) vCodec = t.Substring(idx + 6).Split(new[] { ' ', ',', '(' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                var resMatch = System.Text.RegularExpressions.Regex.Match(t, @"(\d+)x(\d+)");
                if (resMatch.Success) { int.TryParse(resMatch.Groups[1].Value, out width); int.TryParse(resMatch.Groups[2].Value, out height); }
                var fpsMatch = System.Text.RegularExpressions.Regex.Match(t, @"(\d+(\.\d+)?)\s*fps");
                if (fpsMatch.Success) double.TryParse(fpsMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out fps);
            }
            if (t.Contains("Stream #") && t.Contains("Audio:"))
            {
                hasAudio = true;
                var idx = t.IndexOf("Audio:", StringComparison.Ordinal);
                if (idx >= 0) aCodec = t.Substring(idx + 6).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            }
            if (t.StartsWith("Input #0,", StringComparison.Ordinal))
            {
                var m = System.Text.RegularExpressions.Regex.Match(t, @"from '.*\.(\w+)'");
                if (m.Success) container = m.Groups[1].Value;
            }
        }
        return new VideoProbeInfo(input, durationMs, width, height, fps, vCodec, aCodec, container, 0, hasAudio, hasVideo);
    }

    private static double ParseFrameRate(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "0/0") return 0;
        var parts = s.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double num) && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double den) && den != 0)
            return num / den;
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) return v;
        return 0;
    }

    public async Task TranscodeAsync(string inputPath, string outputPath, VideoTranscodeOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException(inputPath);
        string ffmpeg = ResolveFfmpegExe();

        long durationMs = 0;
        try
        {
            var probe = await ProbeAsync(inputPath, ct).ConfigureAwait(false);
            durationMs = probe?.DurationMs ?? 0;
        }
        catch { }

        string tmpPath = BuildTmpPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        bool useTwoPass = options.TwoPassEnabled && options.RateControl == RateControlMode.Bitrate && options.Container != VideoContainer.Gif;
        if (useTwoPass)
        {
            string passLog = Path.Combine(Path.GetTempPath(), $"ffmpeg2pass_{Guid.NewGuid():N}");
            try
            {
                string nullOutput = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
                string format = GetFormatForContainer(options.Container);
                string pass1Args = BuildPass1Arguments(inputPath, passLog, nullOutput, format, options);
                _logger.LogInformation("FFmpeg 2-pass pass1: {Args}", pass1Args);
                await RunFfmpegAsync(ffmpeg, pass1Args, durationMs, new ScaledProgress(progress, 0, 0.5), ct).ConfigureAwait(false);

                string pass2Args = BuildArguments(inputPath, tmpPath, options, pass: 2, passLogFile: passLog);
                _logger.LogInformation("FFmpeg 2-pass pass2: {Args}", pass2Args);
                await RunFfmpegAsync(ffmpeg, pass2Args, durationMs, new ScaledProgress(progress, 0.5, 0.5), ct).ConfigureAwait(false);

                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tmpPath, outputPath);
                progress?.Report(1.0);
                _logger.LogInformation("Transcode 2-pass done: {Input} -> {Output}", inputPath, outputPath);
            }
            finally
            {
                TryDelete(passLog);
                TryDelete(passLog + "-0.log");
                TryDelete(passLog + "-0.log.mbtree");
                TryDelete(tmpPath + ".tmp"); // just in case
                // ffmpeg creates passlog + "-0.log" on Windows
                try
                {
                    foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(passLog) + "*"))
                        TryDelete(f);
                }
                catch { }
            }
            return;
        }

        string args = BuildArguments(inputPath, tmpPath, options);
        _logger.LogInformation("FFmpeg transcode tmp: {Tmp} -> {Final} : {Args}", tmpPath, outputPath, args);
        try
        {
            await RunFfmpegAsync(ffmpeg, args, durationMs, progress, ct).ConfigureAwait(false);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(tmpPath, outputPath);
            progress?.Report(1.0);
            _logger.LogInformation("Transcode done: {Input} -> {Output}", inputPath, outputPath);
        }
        catch
        {
            TryDelete(tmpPath);
            throw;
        }
    }

    private async Task RunFfmpegAsync(string ffmpeg, string args, long durationMs, IProgress<double>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        AppendFfmpegEnv(psi);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<int>();
        var errorBuilder = new StringBuilder();

        proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (errorBuilder) errorBuilder.AppendLine(e.Data);
                TryParseProgress(e.Data, durationMs, progress);
            }
        };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) TryParseProgress(e.Data, durationMs, progress);
        };

        try
        {
            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            using (ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                tcs.TrySetCanceled(ct);
            }))
            {
                int exitCode = await tcs.Task.ConfigureAwait(false);
                await Task.Delay(100, ct).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    string err = errorBuilder.ToString();
                    _logger.LogWarning("FFmpeg failed exit {Code}: {Err}", exitCode, err);
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? $"FFmpeg exit code {exitCode}" : err);
                }
            }
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        finally
        {
            try { proc.Dispose(); } catch { }
        }
    }

    private sealed class ScaledProgress : IProgress<double>
    {
        private readonly IProgress<double>? _inner;
        private readonly double _offset;
        private readonly double _scale;
        public ScaledProgress(IProgress<double>? inner, double offset, double scale) { _inner = inner; _offset = offset; _scale = scale; }
        public void Report(double value) => _inner?.Report(_offset + value * _scale);
    }

    private void AppendFfmpegEnv(ProcessStartInfo psi)
    {
        string? dir = _ffmpeg.ResolvedDirectory;
        if (!string.IsNullOrWhiteSpace(dir))
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = dir + Path.PathSeparator + pathEnv;
            // Linux LD_LIBRARY_PATH
            if (!OperatingSystem.IsWindows())
            {
                string ld = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
                psi.Environment["LD_LIBRARY_PATH"] = dir + Path.PathSeparator + ld;
            }
        }
    }

    private static void TryParseProgress(string line, long durationMs, IProgress<double>? progress)
    {
        if (progress == null || durationMs <= 0) return;
        // ffmpeg -progress pipe:1 emits "out_time_ms=1234567"
        // stderr emits "time=00:00:05.12"
        if (line.StartsWith("out_time_ms=", StringComparison.Ordinal))
        {
            if (long.TryParse(line.Substring("out_time_ms=".Length).Trim(), out long ms))
            {
                double p = Math.Clamp((double)ms / (durationMs * 1000.0), 0, 1);
                progress.Report(p);
            }
        }
        else if (line.Contains("time="))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"time=(\d+):(\d+):(\d+\.\d+)");
            if (m.Success)
            {
                if (int.TryParse(m.Groups[1].Value, out int h) && int.TryParse(m.Groups[2].Value, out int mm) && double.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sec))
                {
                    double totalSec = h * 3600 + mm * 60 + sec;
                    double p = Math.Clamp(totalSec * 1000.0 / durationMs, 0, 1);
                    progress.Report(p);
                }
            }
        }
    }

    private static string BuildTmpPath(string outputPath)
    {
        string dir = Path.GetDirectoryName(outputPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(outputPath);
        string ext = Path.GetExtension(outputPath);
        // Keep extension so ffmpeg can guess format: name.tmp.ext
        return Path.Combine(dir, $"{name}.tmp{ext}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string GetFormatForContainer(VideoContainer c) => c switch
    {
        VideoContainer.Mp4 => "mp4",
        VideoContainer.Mkv => "matroska",
        VideoContainer.WebM => "webm",
        VideoContainer.Mov => "mov",
        VideoContainer.Avi => "avi",
        VideoContainer.Gif => "gif",
        _ => "mp4"
    };

    private static string BuildPass1Arguments(string input, string passLogFile, string nullOutput, string format, VideoTranscodeOptions o)
    {
        var sb = new StringBuilder();
        sb.Append("-y -hide_banner ");
        sb.Append($"-i \"{input}\" ");

        // Filters (same as normal, but without audio)
        var filters = new List<string>();
        if (o.CropEnabled) filters.Add($"crop=iw-{o.CropLeft + o.CropRight}:ih-{o.CropTop + o.CropBottom}:{o.CropLeft}:{o.CropTop}");
        if (o.Deinterlace != DeinterlaceMode.None) filters.Add(o.Deinterlace == DeinterlaceMode.Bwdif ? "bwdif" : "yadif");
        if (o.Denoise != DenoiseMode.None) filters.Add(GetDenoiseFilter(o.Denoise));
        if (o.ScaleMode != ScaleMode.None) filters.Add(BuildScaleFilter(o));
        if (o.FpsMode != FpsMode.SameAsSource)
        {
            if (o.FpsMode == FpsMode.Fixed) filters.Add($"fps={o.FpsValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            else if (o.FpsMode == FpsMode.Peak) filters.Add($"fps={o.FpsValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        if (filters.Count > 0) sb.Append($"-vf \"{string.Join(",", filters)}\" ");

        var (vCodecName, vCodecArgs) = MapVideoCodec(o);
        sb.Append($"-c:v {vCodecName} ");
        if (!string.IsNullOrEmpty(vCodecArgs)) sb.Append($"{vCodecArgs} ");
        sb.Append($"-b:v {o.VideoBitrateKbps}k ");
        sb.Append($"-preset {MapPreset(o.Preset)} ");
        if (o.VideoCodec == VideoCodec.H264 || o.VideoCodec == VideoCodec.H265)
            sb.Append($"-maxrate {o.VideoBitrateKbps}k -bufsize {o.VideoBitrateKbps * 2}k ");
        if (!string.IsNullOrEmpty(o.Profile)) sb.Append($"-profile:v {o.Profile} ");
        if (!string.IsNullOrEmpty(o.Level)) sb.Append($"-level {o.Level} ");
        if (o.FpsMode == FpsMode.Peak) sb.Append($"-r {o.FpsValue.ToString(System.Globalization.CultureInfo.InvariantCulture)} ");

        sb.Append($"-pass 1 -passlogfile \"{passLogFile}\" ");
        sb.Append("-an ");
        sb.Append($"-f {format} -progress pipe:1 -nostats -y \"{nullOutput}\"");
        return sb.ToString();
    }

    private static string BuildArguments(string input, string output, VideoTranscodeOptions o, int? pass = null, string? passLogFile = null)
    {
        var sb = new StringBuilder();
        sb.Append("-y -hide_banner ");
        sb.Append($"-i \"{input}\" ");

        // GIF special handling
        if (o.Container == VideoContainer.Gif)
        {
            // Gif loop and no audio, palettegen/paletteuse
            // Build filter: fps, scale, optional crop/deinterlace/denoise, then split palette
            string fps = o.GifFps > 0 ? o.GifFps.ToString() : "15";
            string width = o.GifWidth > 0 ? o.GifWidth.ToString() : "480";
            var vfParts = new List<string>();
            vfParts.Add($"fps={fps}");
            vfParts.Add($"scale={width}:-1:flags=lanczos");
            if (o.CropEnabled) vfParts.Add($"crop=iw-{o.CropLeft + o.CropRight}:ih-{o.CropTop + o.CropBottom}:{o.CropLeft}:{o.CropTop}");
            if (o.Deinterlace != DeinterlaceMode.None) vfParts.Add(o.Deinterlace == DeinterlaceMode.Bwdif ? "bwdif" : "yadif");
            if (o.Denoise != DenoiseMode.None) vfParts.Add(GetDenoiseFilter(o.Denoise));

            string baseFilters = string.Join(",", vfParts);
            string dither = o.GifDither switch
            {
                GifDither.None => "none",
                GifDither.Bayer => "bayer:bayer_scale=5",
                GifDither.FloydSteinberg => "floyd_steinberg",
                GifDither.Sierpinski => "sierra2_4a",
                _ => "bayer:bayer_scale=5"
            };
            string stats = o.GifStatsMode == "single" ? "single" : "diff";
            string filterComplex = $"{baseFilters},split[s0][s1];[s0]palettegen=max_colors={o.GifMaxColors}:stats_mode={stats}[p];[s1][p]paletteuse=dither={dither}";

            sb.Append($"-filter_complex \"{filterComplex}\" ");
            sb.Append("-loop 0 "); // 0 infinite, ffmpeg gif loop option
            // gif muxer loop
            // For gif, no audio
            sb.Append("-an ");
            sb.Append($"\"{output}\"");
            return sb.ToString();
        }

        // Build video filter chain
        var filters = new List<string>();
        if (o.CropEnabled)
        {
            filters.Add($"crop=iw-{o.CropLeft + o.CropRight}:ih-{o.CropTop + o.CropBottom}:{o.CropLeft}:{o.CropTop}");
        }
        if (o.Deinterlace != DeinterlaceMode.None)
        {
            filters.Add(o.Deinterlace == DeinterlaceMode.Bwdif ? "bwdif" : "yadif");
        }
        if (o.Denoise != DenoiseMode.None)
        {
            filters.Add(GetDenoiseFilter(o.Denoise));
        }
        if (o.ScaleMode != ScaleMode.None)
        {
            filters.Add(BuildScaleFilter(o));
        }
        if (o.FpsMode != FpsMode.SameAsSource)
        {
            if (o.FpsMode == FpsMode.Fixed) filters.Add($"fps={o.FpsValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            else if (o.FpsMode == FpsMode.Peak) filters.Add($"fps={o.FpsValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (filters.Count > 0)
        {
            sb.Append($"-vf \"{string.Join(",", filters)}\" ");
        }

        // Video codec
        var (vCodecName, vCodecArgs) = MapVideoCodec(o);
        sb.Append($"-c:v {vCodecName} ");
        if (!string.IsNullOrEmpty(vCodecArgs)) sb.Append($"{vCodecArgs} ");

        // Rate control
        if (o.RateControl == RateControlMode.Crf)
        {
            string crfArg = MapCrfArg(o.VideoCodec, o.Crf);
            if (!string.IsNullOrEmpty(crfArg)) sb.Append($"{crfArg} ");
            // For x264/x265 CRF, also set preset
            sb.Append($"-preset {MapPreset(o.Preset)} ");
            // Bitrate 0 for CRF
            if (o.VideoCodec == VideoCodec.Vp9 || o.VideoCodec == VideoCodec.Vp8 || o.VideoCodec == VideoCodec.Av1Aom)
                sb.Append("-b:v 0 ");
        }
        else
        {
            sb.Append($"-b:v {o.VideoBitrateKbps}k ");
            sb.Append($"-preset {MapPreset(o.Preset)} ");
            if (o.VideoCodec == VideoCodec.H264 || o.VideoCodec == VideoCodec.H265)
                sb.Append($"-maxrate {o.VideoBitrateKbps}k -bufsize {o.VideoBitrateKbps * 2}k ");
        }

        if (!string.IsNullOrEmpty(o.Profile)) sb.Append($"-profile:v {o.Profile} ");
        if (!string.IsNullOrEmpty(o.Level)) sb.Append($"-level {o.Level} ");

        // Audio
        if (o.AudioCodec == AudioCodec.None)
        {
            sb.Append("-an ");
        }
        else
        {
            var (aCodecName, aArgs) = MapAudioCodec(o.AudioCodec);
            sb.Append($"-c:a {aCodecName} ");
            if (!string.IsNullOrEmpty(aArgs)) sb.Append($"{aArgs} ");
            sb.Append($"-b:a {o.AudioBitrateKbps}k ");
        }

        // For peak fps, use -r as output fps limit
        if (o.FpsMode == FpsMode.Peak)
        {
            sb.Append($"-r {o.FpsValue.ToString(System.Globalization.CultureInfo.InvariantCulture)} ");
        }

        if (pass == 2 && !string.IsNullOrEmpty(passLogFile))
        {
            sb.Append($"-pass 2 -passlogfile \"{passLogFile}\" ");
        }
        else if (pass == 1 && !string.IsNullOrEmpty(passLogFile))
        {
            sb.Append($"-pass 1 -passlogfile \"{passLogFile}\" ");
        }

        // Progress
        sb.Append("-progress pipe:1 -nostats ");

        sb.Append($"\"{output}\"");
        return sb.ToString();
    }

    private static string BuildScaleFilter(VideoTranscodeOptions o)
    {
        // keep aspect handling
        return o.ScaleMode switch
        {
            ScaleMode.FitWithin => $"scale=w={o.ScaleWidth}:h={o.ScaleHeight}:force_original_aspect_ratio=decrease:eval=frame:flags=lanczos",
            ScaleMode.Exact => $"scale={o.ScaleWidth}:{o.ScaleHeight}:flags=lanczos",
            ScaleMode.Width => $"scale={o.ScaleWidth}:-2:flags=lanczos",
            ScaleMode.Height => $"scale=-2:{o.ScaleHeight}:flags=lanczos",
            _ => $"scale={o.ScaleWidth}:{o.ScaleHeight}:flags=lanczos"
        };
    }

    private static string GetDenoiseFilter(DenoiseMode m) => m switch
    {
        DenoiseMode.Hqdn3dLight => "hqdn3d=4:3:6:4.5",
        DenoiseMode.Hqdn3dMedium => "hqdn3d=8:6:8:6",
        DenoiseMode.Hqdn3dStrong => "hqdn3d=12:8:12:8",
        _ => "hqdn3d"
    };

    private static (string Name, string Args) MapVideoCodec(VideoTranscodeOptions o) => o.VideoCodec switch
    {
        VideoCodec.H264 => ("libx264", "-pix_fmt yuv420p"),
        VideoCodec.H265 => ("libx265", "-pix_fmt yuv420p"),
        VideoCodec.Av1Aom => ("libaom-av1", "-pix_fmt yuv420p -strict experimental"),
        VideoCodec.Av1Svt => ("libsvtav1", "-pix_fmt yuv420p"),
        VideoCodec.Vp8 => ("libvpx", "-pix_fmt yuv420p -auto-alt-ref 1 -lag-in-frames 25"),
        VideoCodec.Vp9 => ("libvpx-vp9", "-pix_fmt yuv420p -auto-alt-ref 1 -lag-in-frames 25"),
        VideoCodec.Mpeg4 => ("mpeg4", "-pix_fmt yuv420p"),
        VideoCodec.Gif => ("gif", ""),
        _ => ("libx264", "")
    };

    private static (string Name, string Args) MapAudioCodec(AudioCodec c) => c switch
    {
        AudioCodec.Aac => ("aac", ""),
        AudioCodec.Mp3 => ("libmp3lame", ""),
        AudioCodec.Opus => ("libopus", ""),
        AudioCodec.Vorbis => ("libvorbis", ""),
        AudioCodec.Flac => ("flac", ""),
        AudioCodec.Ac3 => ("ac3", ""),
        _ => ("aac", "")
    };

    private static string MapCrfArg(VideoCodec codec, int crf) => codec switch
    {
        VideoCodec.H264 => $"-crf {crf}",
        VideoCodec.H265 => $"-crf {crf}",
        VideoCodec.Av1Aom => $"-crf {crf}",
        VideoCodec.Av1Svt => $"-crf {crf}",
        VideoCodec.Vp9 => $"-crf {crf}",
        VideoCodec.Vp8 => $"-crf {crf}",
        VideoCodec.Mpeg4 => $"-qscale:v {Math.Clamp(crf / 5, 1, 31)}",
        _ => ""
    };

    private static string MapPreset(PresetLevel p) => p switch
    {
        PresetLevel.Ultrafast => "ultrafast",
        PresetLevel.Superfast => "superfast",
        PresetLevel.Veryfast => "veryfast",
        PresetLevel.Faster => "faster",
        PresetLevel.Fast => "fast",
        PresetLevel.Medium => "medium",
        PresetLevel.Slow => "slow",
        PresetLevel.Slower => "slower",
        PresetLevel.Veryslow => "veryslow",
        PresetLevel.Placebo => "placebo",
        _ => "medium"
    };
}
