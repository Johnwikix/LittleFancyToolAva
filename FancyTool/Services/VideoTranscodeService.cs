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
                var parts = t.Split(',');
                var durPart = parts[0].Replace("Duration:", "").Trim();
                if (TimeSpan.TryParse(durPart, out var ts)) durationMs = (long)ts.TotalMilliseconds;
            }
            if (t.Contains("Stream #") && t.Contains("Video:"))
            {
                hasVideo = true;
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

        // Source pix_fmt detection for 10-bit / 422 / 444 passthrough (source-透传)
        string? sourcePixFmt = null;
        try { sourcePixFmt = await GetSourcePixFmtAsync(inputPath, ct).ConfigureAwait(false); }
        catch { }

        string tmpPath = BuildTmpPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        bool useTwoPass = options.TwoPassEnabled && options.RateControl == RateControlMode.Bitrate && options.Container != VideoContainer.Gif && options.HardwareBackend == HardwareBackend.Software;
        if (useTwoPass)
        {
            string passLog = Path.Combine(Path.GetTempPath(), $"ffmpeg2pass_{Guid.NewGuid():N}");
            try
            {
                string nullOutput = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
                string format = GetFormatForContainer(options.Container);
                string pass1Args = BuildPass1Arguments(inputPath, passLog, nullOutput, format, options, sourcePixFmt);
                _logger.LogInformation("FFmpeg 2-pass pass1: {Args}", pass1Args);
                await RunFfmpegAsync(ffmpeg, pass1Args, durationMs, new ScaledProgress(progress, 0, 0.5), ct).ConfigureAwait(false);

                string pass2Args = BuildArguments(inputPath, tmpPath, options, sourcePixFmt, pass: 2, passLogFile: passLog);
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
                TryDelete(tmpPath + ".tmp");
                try
                {
                    foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(passLog) + "*"))
                        TryDelete(f);
                }
                catch { }
            }
            return;
        }

        string args = BuildArguments(inputPath, tmpPath, options, sourcePixFmt);
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

    private async Task<string?> GetSourcePixFmtAsync(string input, CancellationToken ct)
    {
        string ffprobe = ResolveFfprobeExe();
        bool isFfprobe = Path.GetFileNameWithoutExtension(ffprobe).Equals("ffprobe", StringComparison.OrdinalIgnoreCase);
        if (!isFfprobe) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 \"{input}\"",
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
            await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0) return null;
            string fmt = output.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
            if (string.IsNullOrEmpty(fmt) || fmt == "unknown") return null;
            // ffprobe may return like "yuv420p" plus profile suffix — take first token
            fmt = fmt.Split(new[] { ' ', ',', '(' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? fmt;
            return fmt;
        }
        catch
        {
            return null;
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
        VideoContainer.Mov => "mov",
        VideoContainer.Avi => "avi",
        VideoContainer.Gif => "gif",
        _ => "mp4"
    };

    // ==================== Source pix_fmt → target pix_fmt (10-bit / 422 / 444 透传) ====================

    private static (int W, int H, int Depth) ParsePixFmt(string pixFmt)
    {
        string s = pixFmt.Trim().ToLowerInvariant();
        // strip profile suffix like "yuv420p(tv, progressive)"
        int paren = s.IndexOf('(');
        if (paren >= 0) s = s.Substring(0, paren).Trim();
        return s switch
        {
            "yuv420p" => (1, 1, 8),
            "yuvj420p" => (1, 1, 8),
            "yuv420p10le" => (1, 1, 10),
            "yuv420p10be" => (1, 1, 10),
            "yuv420p12le" => (1, 1, 12),
            "yuv420p12be" => (1, 1, 12),
            "yuv420p16le" => (1, 1, 12),
            "yuv422p" => (1, 0, 8),
            "yuvj422p" => (1, 0, 8),
            "yuv422p10le" => (1, 0, 10),
            "yuv422p10be" => (1, 0, 10),
            "yuv422p12le" => (1, 0, 12),
            "yuv444p" => (0, 0, 8),
            "yuvj444p" => (0, 0, 8),
            "yuv444p10le" => (0, 0, 10),
            "yuv444p10be" => (0, 0, 10),
            "yuv444p12le" => (0, 0, 12),
            "nv12" => (1, 1, 8),
            "nv21" => (1, 1, 8),
            "p010le" => (1, 1, 10),
            "p010be" => (1, 1, 10),
            "p012le" => (1, 1, 12),
            "yuyv422" => (1, 0, 8),
            "y210le" => (1, 0, 10),
            "y212le" => (1, 0, 12),
            "vuya" => (0, 0, 8),
            "gbrp" => (0, 0, 8),
            "gbrp10le" => (0, 0, 10),
            "gbrp12le" => (0, 0, 12),
            "rgb24" => (0, 0, 8),
            "bgr24" => (0, 0, 8),
            "rgba" => (0, 0, 8),
            "bgra" => (0, 0, 8),
            _ => (1, 1, 8)
        };
    }

    private static string? PlanarPixFmtName(int w, int h, int d) => (w, h, d) switch
    {
        (1, 1, 8) => "yuv420p",
        (1, 1, 10) => "yuv420p10le",
        (1, 1, 12) => "yuv420p12le",
        (1, 0, 8) => "yuv422p",
        (1, 0, 10) => "yuv422p10le",
        (1, 0, 12) => "yuv422p12le",
        (0, 0, 8) => "yuv444p",
        (0, 0, 10) => "yuv444p10le",
        (0, 0, 12) => "yuv444p12le",
        _ => null
    };

    private static bool IsQsvBackend(HardwareBackend hw) => hw == HardwareBackend.Intel && !IsVaapiBackend(hw);

    private static string? PickTargetPixFmt(VideoTranscodeOptions o, string? sourcePixFmt)
    {
        if (o.Container == VideoContainer.Gif) return null;
        if (IsVaapiBackend(o.HardwareBackend)) return null; // vaapi via filter
        if (string.IsNullOrWhiteSpace(sourcePixFmt)) return "yuv420p";

        var (srcW, srcH, srcDepth) = ParsePixFmt(sourcePixFmt);
        if (srcDepth < 8) srcDepth = 8;

        // QSV system-memory path (mirrors previous DLL ladder at VideoTranscodeService.cs:1185)
        if (IsQsvBackend(o.HardwareBackend))
        {
            if (o.VideoCodec == VideoCodec.H265)
            {
                if (srcW == 1 && srcH == 0)
                {
                    if (srcDepth >= 10) return "y210le";
                    return "yuyv422";
                }
                if (srcDepth >= 10) return "p010le";
                return "nv12";
            }
            return "nv12"; // h264_qsv / av1_qsv forced nv12
        }

        // Software + NVENC + AMF : same chroma/depth preferred, then downscale; H264 capped at 8-bit
        bool allowHighDepth = o.VideoCodec != VideoCodec.H264;
        var depths = new List<int>();
        foreach (var d in new[] { srcDepth, 10, 8 })
            if (d <= srcDepth && !depths.Contains(d)) depths.Add(d);
        var chromas = new List<(int W, int H)> { (srcW, srcH) };
        if (srcW != 1 || srcH != 1) chromas.Add((1, 1));

        foreach (var (w, h) in chromas)
        {
            foreach (var d in depths)
            {
                if (!allowHighDepth && d > 8) continue;
                string? name = PlanarPixFmtName(w, h, d);
                if (name != null) return name;
            }
        }
        return "yuv420p";
    }

    // ==================== Argument builders ====================

    private static string BuildPass1Arguments(string input, string passLogFile, string nullOutput, string format, VideoTranscodeOptions o, string? sourcePixFmt)
    {
        var sb = new StringBuilder();
        sb.Append("-y -hide_banner ");
        if (IsVaapiBackend(o.HardwareBackend))
            sb.Append("-vaapi_device /dev/dri/renderD128 ");
        sb.Append($"-i \"{input}\" ");

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
        if (IsVaapiBackend(o.HardwareBackend)) filters.Add("format=nv12,hwupload");
        else
        {
            string? pix = PickTargetPixFmt(o, sourcePixFmt);
            if (!string.IsNullOrEmpty(pix) && pix != "yuv420p")
            {
                // For pass1, still enforce target pix via format filter to keep behavior consistent
                // but avoid adding duplicate format if scaler already covers it
                // Use separate format filter only when needed for depth/chroma preservation
            }
        }
        if (filters.Count > 0) sb.Append($"-vf \"{string.Join(",", filters)}\" ");

        var (vCodecName, vCodecArgs) = MapVideoCodec(o);
        sb.Append($"-c:v {vCodecName} ");
        if (!string.IsNullOrEmpty(vCodecArgs)) sb.Append($"{vCodecArgs} ");
        // pix_fmt for pass1
        if (!IsVaapiBackend(o.HardwareBackend))
        {
            string? pix = PickTargetPixFmt(o, sourcePixFmt);
            if (!string.IsNullOrEmpty(pix)) sb.Append($"-pix_fmt {pix} ");
        }
        sb.Append($"-b:v {o.VideoBitrateKbps}k ");
        sb.Append($"-preset {MapPreset(o.Preset, o.HardwareBackend)} ");
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

    private static string BuildArguments(string input, string output, VideoTranscodeOptions o, string? sourcePixFmt, int? pass = null, string? passLogFile = null)
    {
        var sb = new StringBuilder();
        sb.Append("-y -hide_banner ");
        if (IsVaapiBackend(o.HardwareBackend))
            sb.Append("-vaapi_device /dev/dri/renderD128 ");
        sb.Append($"-i \"{input}\" ");

        if (o.Container == VideoContainer.Gif)
        {
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
            sb.Append("-loop 0 ");
            sb.Append("-an ");
            sb.Append($"\"{output}\"");
            return sb.ToString();
        }

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
        if (IsVaapiBackend(o.HardwareBackend)) filters.Add("format=nv12,hwupload");

        if (filters.Count > 0)
        {
            sb.Append($"-vf \"{string.Join(",", filters)}\" ");
        }

        var (vCodecName, vCodecArgs) = MapVideoCodec(o);
        sb.Append($"-c:v {vCodecName} ");
        if (!string.IsNullOrEmpty(vCodecArgs)) sb.Append($"{vCodecArgs} ");
        if (!IsVaapiBackend(o.HardwareBackend))
        {
            string? pix = PickTargetPixFmt(o, sourcePixFmt);
            if (!string.IsNullOrEmpty(pix)) sb.Append($"-pix_fmt {pix} ");
        }

        if (o.RateControl == RateControlMode.Crf)
        {
            string crfArg = MapCrfArg(o.VideoCodec, o.Crf, o.HardwareBackend);
            if (!string.IsNullOrEmpty(crfArg)) sb.Append($"{crfArg} ");
            sb.Append($"-preset {MapPreset(o.Preset, o.HardwareBackend)} ");
            if (o.VideoCodec == VideoCodec.Vp9 || o.VideoCodec == VideoCodec.Vp8 || o.VideoCodec == VideoCodec.Av1Aom)
                sb.Append("-b:v 0 ");
        }
        else
        {
            sb.Append($"-b:v {o.VideoBitrateKbps}k ");
            sb.Append($"-preset {MapPreset(o.Preset, o.HardwareBackend)} ");
            if (o.VideoCodec == VideoCodec.H264 || o.VideoCodec == VideoCodec.H265)
                sb.Append($"-maxrate {o.VideoBitrateKbps}k -bufsize {o.VideoBitrateKbps * 2}k ");
        }

        if (!string.IsNullOrEmpty(o.Profile)) sb.Append($"-profile:v {o.Profile} ");
        if (!string.IsNullOrEmpty(o.Level)) sb.Append($"-level {o.Level} ");

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
            if (Math.Abs(o.AudioGainDb) > 0.01)
                sb.Append($"-filter:a \"volume={o.AudioGainDb.ToString(System.Globalization.CultureInfo.InvariantCulture)}dB\" ");
        }

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

        sb.Append("-progress pipe:1 -nostats ");

        sb.Append($"\"{output}\"");
        return sb.ToString();
    }

    private static string BuildScaleFilter(VideoTranscodeOptions o)
    {
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

    private static bool IsVaapiBackend(HardwareBackend hw) => (hw == HardwareBackend.Intel || hw == HardwareBackend.Amd) && !OperatingSystem.IsWindows();

    private static string MapHardwareEncoder(VideoCodec codec, HardwareBackend hw)
    {
        bool isVaapi = IsVaapiBackend(hw);
        return (codec, hw, isVaapi) switch
        {
            (VideoCodec.H264, HardwareBackend.Nvidia, _) => "h264_nvenc",
            (VideoCodec.H265, HardwareBackend.Nvidia, _) => "hevc_nvenc",
            (VideoCodec.Av1Aom, HardwareBackend.Nvidia, _) => "av1_nvenc",
            (VideoCodec.Av1Svt, HardwareBackend.Nvidia, _) => "av1_nvenc",
            (VideoCodec.H264, HardwareBackend.Intel, false) => "h264_qsv",
            (VideoCodec.H265, HardwareBackend.Intel, false) => "hevc_qsv",
            (VideoCodec.Av1Aom, HardwareBackend.Intel, false) => "av1_qsv",
            (VideoCodec.Av1Svt, HardwareBackend.Intel, false) => "av1_qsv",
            (VideoCodec.H264, HardwareBackend.Amd, false) => "h264_amf",
            (VideoCodec.H265, HardwareBackend.Amd, false) => "hevc_amf",
            (VideoCodec.Av1Aom, HardwareBackend.Amd, false) => "av1_amf",
            (VideoCodec.Av1Svt, HardwareBackend.Amd, false) => "av1_amf",
            (VideoCodec.H264, _, true) => "h264_vaapi",
            (VideoCodec.H265, _, true) => "hevc_vaapi",
            (VideoCodec.Av1Aom, _, true) => "av1_vaapi",
            (VideoCodec.Av1Svt, _, true) => "av1_vaapi",
            (VideoCodec.Vp8, _, true) => "vp8_vaapi",
            (VideoCodec.Vp9, _, true) => "vp9_vaapi",
            _ => ""
        };
    }

    private static (string Name, string Args) MapVideoCodec(VideoTranscodeOptions o)
    {
        if (o.HardwareBackend != HardwareBackend.Software && o.Container != VideoContainer.Gif)
        {
            string hwEnc = MapHardwareEncoder(o.VideoCodec, o.HardwareBackend);
            if (!string.IsNullOrEmpty(hwEnc))
            {
                return (hwEnc, "");
            }
        }
        return o.VideoCodec switch
        {
            VideoCodec.H264 => ("libx264", ""),
            VideoCodec.H265 => ("libx265", ""),
            VideoCodec.Av1Aom => ("libaom-av1", "-strict experimental"),
            VideoCodec.Av1Svt => ("libsvtav1", ""),
            VideoCodec.Vp8 => ("libvpx", "-auto-alt-ref 1 -lag-in-frames 25"),
            VideoCodec.Vp9 => ("libvpx-vp9", "-auto-alt-ref 1 -lag-in-frames 25"),
            VideoCodec.Mpeg4 => ("mpeg4", ""),
            VideoCodec.Gif => ("gif", ""),
            _ => ("libx264", "")
        };
    }

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

    private static string MapCrfArg(VideoCodec codec, int crf, HardwareBackend hw = HardwareBackend.Software)
    {
        if (hw != HardwareBackend.Software)
        {
            return hw switch
            {
                HardwareBackend.Nvidia => $"-qp {crf} -rc constqp",
                HardwareBackend.Intel when !IsVaapiBackend(hw) => $"-global_quality {crf}",
                HardwareBackend.Amd when !IsVaapiBackend(hw) => $"-qp_i {crf} -qp_p {crf}",
                _ when IsVaapiBackend(hw) => $"-qp {crf}",
                _ => $"-qp {crf}"
            };
        }
        return codec switch
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
    }

    private static string MapPreset(PresetLevel p, HardwareBackend hw = HardwareBackend.Software)
    {
        if (hw == HardwareBackend.Nvidia)
        {
            return p switch
            {
                PresetLevel.Ultrafast => "p1",
                PresetLevel.Superfast => "p2",
                PresetLevel.Veryfast => "p3",
                PresetLevel.Faster => "p4",
                PresetLevel.Fast => "p4",
                PresetLevel.Medium => "p5",
                PresetLevel.Slow => "p6",
                PresetLevel.Slower => "p6",
                PresetLevel.Veryslow => "p7",
                PresetLevel.Placebo => "p7",
                _ => "p5"
            };
        }
        if (hw == HardwareBackend.Amd && !IsVaapiBackend(hw))
        {
            return p switch
            {
                PresetLevel.Ultrafast => "speed",
                PresetLevel.Superfast => "speed",
                PresetLevel.Veryfast => "speed",
                PresetLevel.Faster => "balanced",
                PresetLevel.Fast => "balanced",
                PresetLevel.Medium => "balanced",
                PresetLevel.Slow => "quality",
                PresetLevel.Slower => "quality",
                PresetLevel.Veryslow => "quality",
                PresetLevel.Placebo => "quality",
                _ => "balanced"
            };
        }
        return p switch
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
}
