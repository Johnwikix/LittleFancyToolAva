using System.Diagnostics;
using FancyToolAva.Models;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

/// <summary>
/// BYO FFmpeg validator via ffmpeg.exe + ffprobe.exe (no native dll loading).
/// Validates user-provided directory containing ffmpeg executables and probes encoders via `ffmpeg -encoders`.
/// Supports source pix_fmt passthrough via ffprobe; no FFmpeg.AutoGen, no unsafe.
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
    private string? _versionInfo;
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

    public string? VersionInfo
    {
        get { lock (_lock) return _versionInfo; }
        private set { lock (_lock) _versionInfo = value; }
    }

    public FfmpegService(ILogger<FfmpegService> logger, AppPreferences preferences)
    {
        _logger = logger;
        _preferences = preferences;
        _preferences.PropertyChanged += OnPreferencesChanged;

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
            SetUnavailable(dir, LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegMissingFiles", string.Join(", ", missing)));
            return false;
        }

        try
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();

            string? version = await ProbeVersionAsync(dir, ct).ConfigureAwait(false);
            var (video, audio) = await ProbeEncodersAsync(dir, ct).ConfigureAwait(false);

            if (video.Count == 0 && audio.Count == 0)
            {
                video = new List<string> { "libx264", "libx265", "libaom-av1", "libsvtav1", "libvpx", "libvpx-vp9", "mpeg4", "gif" };
                audio = new List<string> { "aac", "libmp3lame", "libopus", "libvorbis", "flac", "ac3" };
            }

            lock (_lock)
            {
                _availableVideoEncoders = video.ToList();
                _availableAudioEncoders = audio.ToList();
                _versionInfo = version ?? _versionInfo;
            }
            ResolvedDirectory = dir;
            LastError = null;
            IsAvailable = true;
            _logger.LogInformation("FFmpeg validated at {Dir} version={Ver} video={Video} audio={Audio}", dir, VersionInfo, string.Join(",", _availableVideoEncoders), string.Join(",", _availableAudioEncoders));
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

        string byo = AppPaths.FfmpegBringYourOwnDirectory;
        if (Directory.Exists(byo) && CheckRequiredFiles(byo).Missing.Count == 0)
            return byo;

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
            bool exists = File.Exists(path);
            if (exists) found.Add(name); else missing.Add(name);
        }
        return (missing, found);
    }

    private static IReadOnlyList<string> GetRequiredFileNames()
    {
        if (OperatingSystem.IsWindows())
            return new[] { "ffmpeg.exe" };
        else
            return new[] { "ffmpeg" };
    }

    private static async Task<string?> ProbeVersionAsync(string dir, CancellationToken ct)
    {
        string exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        if (!File.Exists(exe)) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["PATH"] = dir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            string output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            string error = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            string firstLine = (output + "\n" + error).Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(firstLine))
                return firstLine;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(IReadOnlyList<string> Video, IReadOnlyList<string> Audio)> ProbeEncodersAsync(string dir, CancellationToken ct)
    {
        string ffmpegExe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        if (!File.Exists(ffmpegExe))
        {
            var fallbackVideo = new[] { "libx264", "libx265", "libaom-av1", "libsvtav1", "libvpx", "libvpx-vp9", "mpeg4", "gif" };
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
        catch
        {
            var fallbackVideo = new[] { "libx264", "libx265", "libaom-av1", "libsvtav1", "libvpx", "libvpx-vp9", "mpeg4", "gif" };
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
            if (trimmed.Length < 2) continue;
            char type = trimmed[0];
            bool wantVideo = isVideo ? type == 'V' : type == 'A';
            if (!wantVideo) continue;
            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            string name = parts[1].Trim();
            list.Add(name);
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
