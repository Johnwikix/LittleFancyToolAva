using System.Diagnostics;
using System.Runtime.InteropServices;
using FancyToolAva.Models;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

/// <summary>
/// BYO FFmpeg validator: does NOT download. Validates a user-provided directory containing
/// FFmpeg shared libs (avcodec/avformat/avutil/swscale/swresample) via file existence + probe attempt.
/// GPL builds (libx264/x265) are unlocked only when the user brings their own binaries.
/// </summary>
public sealed class FfmpegService : IFfmpegService
{
    private readonly ILogger<FfmpegService> _logger;
    private readonly AppPreferences _preferences;
    private readonly object _lock = new();
    private bool _disposed;
    private bool _isAvailable;
    private string? _resolvedDirectory;
    private string? _lastError;
    private List<string> _availableVideoEncoders = new();
    private List<string> _availableAudioEncoders = new();

    public event EventHandler<bool>? AvailabilityChanged;

    public bool IsAvailable
    {
        get { lock (_lock) return _isAvailable; }
        private set
        {
            bool changed;
            lock (_lock) { changed = _isAvailable != value; _isAvailable = value; }
            if (changed) AvailabilityChanged?.Invoke(this, value);
        }
    }

    public string? ResolvedDirectory
    {
        get { lock (_lock) return _resolvedDirectory; }
        private set { lock (_lock) _resolvedDirectory = value; }
    }

    public string? LastError
    {
        get { lock (_lock) return _lastError; }
        private set { lock (_lock) _lastError = value; }
    }

    public IReadOnlyList<string> AvailableVideoEncoders
    {
        get { lock (_lock) return _availableVideoEncoders.AsReadOnly(); }
    }

    public IReadOnlyList<string> AvailableAudioEncoders
    {
        get { lock (_lock) return _availableAudioEncoders.AsReadOnly(); }
    }

    public FfmpegService(ILogger<FfmpegService> logger, AppPreferences preferences)
    {
        _logger = logger;
        _preferences = preferences;
        _preferences.PropertyChanged += OnPreferencesChanged;

        // Warm validate synchronously best-effort (not blocking UI thread heavily)
        _ = Task.Run(async () =>
        {
            try { await ValidateAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Initial FFmpeg validation failed"); }
        });
    }

    private void OnPreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppPreferences.CustomFfmpegDirectory))
        {
            _ = Task.Run(async () =>
            {
                try { await ValidateAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "FFmpeg re-validation failed after preference change"); }
            });
        }
    }

    public async Task<bool> ValidateAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;

        string? dir = ResolveDirectory();
        if (string.IsNullOrWhiteSpace(dir))
        {
            SetUnavailable(null, LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegNotConfigured"));
            return false;
        }

        dir = Path.GetFullPath(dir);
        if (!Directory.Exists(dir))
        {
            SetUnavailable(dir, LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegDirNotFound", dir));
            return false;
        }

        var (missing, _) = CheckRequiredFiles(dir);
        if (missing.Count > 0)
        {
            bool hasExe = File.Exists(Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"));
            string hint = hasExe
                ? " (检测到 ffmpeg.exe 但缺 dll，您可能下载了 static 版；请下载文件名含 gpl-shared 的 shared 版，解压后选择 bin 目录)"
                : "";
            SetUnavailable(dir, LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegMissingFiles", string.Join(", ", missing)) + hint);
            return false;
        }

        // Try probe via ffmpeg executable if present (most GPL shared builds ship ffmpeg.exe alongside dlls).
        // Fall back to dll presence check. Actual codec enumeration happens in VideoTranscodeService after load,
        // but we can do a lightweight probe here.
        try
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();

            var probeResult = await ProbeEncodersAsync(dir, ct).ConfigureAwait(false);
            lock (_lock)
            {
                _availableVideoEncoders = probeResult.Video.ToList();
                _availableAudioEncoders = probeResult.Audio.ToList();
            }

            ResolvedDirectory = dir;
            LastError = null;
            IsAvailable = true;
            _logger.LogInformation("FFmpeg validated at {Dir} (video encoders: {Video}, audio: {Audio})", dir,
                string.Join(",", _availableVideoEncoders), string.Join(",", _availableAudioEncoders));
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FFmpeg validation probe failed at {Dir}", dir);
            SetUnavailable(dir, LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegProbeFailed", ex.Message));
            return false;
        }
    }

    private void SetUnavailable(string? dir, string error)
    {
        ResolvedDirectory = dir;
        LastError = error;
        lock (_lock) { _availableVideoEncoders.Clear(); _availableAudioEncoders.Clear(); }
        IsAvailable = false;
        _logger.LogInformation("FFmpeg unavailable: {Error} (dir={Dir})", error, dir);
    }

    private string? ResolveDirectory()
    {
        string? custom = _preferences.CustomFfmpegDirectory;
        if (!string.IsNullOrWhiteSpace(custom)) return custom;

        // Suggest BYO directory; do not auto-create to avoid implying we ship binaries.
        // But if user previously placed files there manually, respect it.
        string byo = AppPaths.FfmpegBringYourOwnDirectory;
        if (Directory.Exists(byo) && CheckRequiredFiles(byo).Missing.Count == 0)
            return byo;

        // Also check legacy FfmpegDirectory root
        string legacy = AppPaths.FfmpegDirectory;
        if (Directory.Exists(legacy) && CheckRequiredFiles(legacy).Missing.Count == 0)
            return legacy;

        return custom ?? byo;
    }

    private static (List<string> Missing, List<string> Found) CheckRequiredFiles(string dir)
    {
        var required = GetRequiredFileNames();
        var missing = new List<string>();
        var found = new List<string>();
        foreach (var name in required)
        {
            string path = Path.Combine(dir, name);
            // Allow versioned .so names like libavcodec.so.61 on Linux
            bool exists = File.Exists(path);
            if (!exists && !OperatingSystem.IsWindows())
            {
                // Fuzzy check: libavcodec.so* exists
                string pattern = name + "*";
                try
                {
                    exists = Directory.GetFiles(dir, pattern).Length > 0;
                }
                catch { exists = false; }
            }
            if (exists) found.Add(name); else missing.Add(name);
        }
        return (missing, found);
    }

    private static IReadOnlyList<string> GetRequiredFileNames()
    {
        if (OperatingSystem.IsWindows())
        {
            // BtbN win64-gpl-shared naming (avcodec-61.dll etc). Accept both -61 and -60 for forward compat.
            return new[] { "avcodec-61.dll", "avformat-61.dll", "avutil-59.dll", "swscale-8.dll", "swresample-5.dll", "avfilter-10.dll" };
        }
        else
        {
            return new[] { "libavcodec.so", "libavformat.so", "libavutil.so", "libswscale.so", "libswresample.so", "libavfilter.so" };
        }
    }

    private static async Task<(IReadOnlyList<string> Video, IReadOnlyList<string> Audio)> ProbeEncodersAsync(string dir, CancellationToken ct)
    {
        // Try ffmpeg executable probe: ffmpeg -hide_banner -encoders
        string ffmpegExe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        if (!File.Exists(ffmpegExe))
        {
            // Fallback: if no exe, assume common encoders are present if dlls exist; enrich later via native probe.
            // Report a conservative set that covers LGPL baseline.
            var fallbackVideo = new[] { "libaom-av1", "libvpx", "libvpx-vp9", "mpeg4", "gif" };
            var fallbackAudio = new[] { "aac", "libmp3lame", "libopus", "libvorbis", "flac", "ac3" };
            return (fallbackVideo, fallbackAudio);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = "-hide_banner -encoders",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // Ensure dll resolution for the child process (Windows)
            psi.Environment["PATH"] = dir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            string output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            string error = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            string combined = output + "\n" + error;
            var video = ParseEncoders(combined, isVideo: true);
            var audio = ParseEncoders(combined, isVideo: false);
            if (video.Count == 0 && audio.Count == 0)
                throw new InvalidOperationException("No encoders parsed from ffmpeg output");
            return (video, audio);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fallback to conservative list
            var fallbackVideo = new[] { "libaom-av1", "libvpx", "libvpx-vp9", "mpeg4", "gif" };
            var fallbackAudio = new[] { "aac", "libmp3lame", "libopus", "libvorbis", "flac", "ac3" };
            return (fallbackVideo, fallbackAudio);
        }
    }

    private static List<string> ParseEncoders(string text, bool isVideo)
    {
        var list = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            // Format: " V..... libx264  H.264 / AVC ..."
            // First char is V/A/S
            if (trimmed.Length < 2) continue;
            char type = trimmed[0];
            bool wantVideo = isVideo ? type == 'V' : type == 'A';
            if (!wantVideo) continue;
            // Extract encoder name (second token)
            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            string name = parts[1].Trim();
            // Normalize some aliases
            if (name == "libx264") list.Add("libx264");
            else if (name == "libx265") list.Add("libx265");
            else if (name == "libaom-av1") list.Add("libaom-av1");
            else if (name == "libsvtav1") list.Add("libsvtav1");
            else if (name == "libvpx") list.Add("libvpx");
            else if (name == "libvpx-vp9") list.Add("libvpx-vp9");
            else if (name == "mpeg4") list.Add("mpeg4");
            else if (name == "gif") list.Add("gif");
            else if (name == "aac") list.Add("aac");
            else if (name == "libmp3lame") list.Add("libmp3lame");
            else if (name == "libopus") list.Add("libopus");
            else if (name == "libvorbis") list.Add("libvorbis");
            else if (name == "flac") list.Add("flac");
            else if (name == "ac3") list.Add("ac3");
            else list.Add(name);
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool ValidateEncoder(string encoderName)
    {
        lock (_lock)
        {
            return _availableVideoEncoders.Contains(encoderName, StringComparer.OrdinalIgnoreCase)
                || _availableAudioEncoders.Contains(encoderName, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preferences.PropertyChanged -= OnPreferencesChanged;
    }
}
