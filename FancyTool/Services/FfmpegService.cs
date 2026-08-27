using FancyToolAva.Models;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

/// <summary>
/// BYO FFmpeg validator via FFmpeg.AutoGen 9.0 native libs (avcodec-63 etc).
/// No ffmpeg.exe required. Validates shared libs via native dlopen + encoder enumeration.
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

    public string? VersionInfo => FfmpegNativeLoader.VersionInfo;

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

            var (ok, video, audio, loadError) = TryValidateNative(dir);

            if (!ok)
            {
                SetUnavailable(dir, LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegProbeFailed", loadError ?? "native load failed"));
                return false;
            }

            if (video.Count == 0 && audio.Count == 0)
            {
                video = new List<string> { "libx264", "libx265", "libaom-av1", "libsvtav1", "gif" };
                audio = new List<string> { "aac", "libmp3lame", "libopus", "libvorbis", "flac", "ac3" };
            }

            lock (_lock)
            {
                _availableVideoEncoders = video.ToList();
                _availableAudioEncoders = audio.ToList();
            }
            ResolvedDirectory = dir;
            LastError = null;
            IsAvailable = true;
            _logger.LogInformation("FFmpeg native validated at {Dir} version={Ver} video={Video} audio={Audio}", dir, FfmpegNativeLoader.VersionInfo, string.Join(",", _availableVideoEncoders), string.Join(",", _availableAudioEncoders));
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

    private (bool Ok, IReadOnlyList<string> Video, IReadOnlyList<string> Audio, string? Error) TryValidateNative(string dir)
    {
        // This method contains unsafe code but is not async, so it's allowed
        unsafe
        {
            if (!FfmpegNativeLoader.TryInitialize(dir, _logger, out string? loadError))
            {
                return (false, Array.Empty<string>(), Array.Empty<string>(), loadError);
            }
            var (video, audio) = FfmpegNativeLoader.EnumerateEncoders();
            return (true, video, audio, null);
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
        var bases = GetRequiredBaseNames();
        var missing = new List<string>();
        var found = new List<string>();
        foreach (var baseName in bases)
        {
            bool exists;
            if (OperatingSystem.IsWindows())
            {
                try { exists = Directory.GetFiles(dir, baseName + "-*.dll").Length > 0; }
                catch { exists = false; }
            }
            else
            {
                try { exists = Directory.GetFiles(dir, baseName + ".so*").Length > 0; }
                catch { exists = false; }
            }
            if (exists) found.Add(baseName); else missing.Add(baseName);
        }
        return (missing, found);
    }

    private static IReadOnlyList<string> GetRequiredBaseNames()
    {
        return new[] { "avcodec", "avformat", "avutil", "swscale", "swresample", "avfilter" };
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
