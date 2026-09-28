using FancyToolAva.Models;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

/// <summary>
/// 进程内 FFmpeg 校验：确认随应用分发的 shared 库（avcodec-63/avformat-63/avutil-61/
/// avfilter-12/swscale-10/swresample-7）可加载，并枚举可用的视频/音频编码器。
/// 无需用户下载或配置外部 ffmpeg.exe。
/// </summary>
public sealed class FfmpegService : IFfmpegService
{
    private static readonly string[] FallbackVideoEncoders =
        ["libx264", "libx265", "libaom-av1", "libsvtav1", "libvpx", "libvpx-vp9", "mpeg4", "gif",
         "h264_nvenc", "hevc_nvenc", "av1_nvenc", "h264_qsv", "hevc_qsv", "av1_qsv", "h264_amf", "hevc_amf", "av1_amf"];
    private static readonly string[] FallbackAudioEncoders =
        ["aac", "libmp3lame", "libopus", "libvorbis", "flac", "ac3"];

    private readonly ILogger<FfmpegService> _logger;
    private readonly object _lock = new();
    private bool _disposed;
    private bool _isAvailable;
    private string? _lastError;
    private string? _versionInfo;
    private List<string> _availableVideoEncoders = [];
    private List<string> _availableAudioEncoders = [];

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

    public FfmpegService(ILogger<FfmpegService> logger)
    {
        _logger = logger;
        _ = Task.Run(async () =>
        {
            try { await ValidateAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Initial FFmpeg validation failed"); }
        });
    }

    public Task<bool> ValidateAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult(false);
        ct.ThrowIfCancellationRequested();

        bool ok = FFmpegNative.TryLoad(out Exception? loadError);
        if (!ok)
        {
            LastError = LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegLoadFailed", loadError!.Message);
            lock (_lock) { _availableVideoEncoders.Clear(); _availableAudioEncoders.Clear(); }
            IsAvailable = false;
            _logger.LogWarning("Bundled FFmpeg failed to load: {Error}", loadError);
            return Task.FromResult(false);
        }

        string version = FFmpegNative.GetVersionInfo();
        var (video, audio) = FFmpegNative.EnumerateEncoders();
        if (video.Count == 0 && audio.Count == 0)
        {
            video = FallbackVideoEncoders;
            audio = FallbackAudioEncoders;
        }

        lock (_lock)
        {
            _availableVideoEncoders = video.ToList();
            _availableAudioEncoders = audio.ToList();
            _versionInfo = version;
        }
        LastError = null;
        IsAvailable = true;
        _logger.LogInformation("FFmpeg loaded in-process: {Version} video={Video} audio={Audio}",
            VersionInfo, _availableVideoEncoders.Count, _availableAudioEncoders.Count);
        return Task.FromResult(true);
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
    }
}
