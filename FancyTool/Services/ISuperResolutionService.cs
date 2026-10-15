using SkiaSharp;

namespace FancyToolAva.Services;

public enum SuperResolutionModel
{
    RealEsrganX4Plus = 0,
    RealEsrganX4PlusAnime = 1,
    RealEsrganGeneralX4V3 = 2,
    UltraSharpX4 = 3,
    SpanClearRealityV1 = 4,
    /// <summary>Uses the native 2× live-action SPAN model under its noncommercial license.</summary>
    LiveActionSpanX2 = 5,
    /// <summary>Video-only bidirectional temporal super-resolution over five adjacent frames.</summary>
    NanoVsr644K = 6
}

public interface ISuperResolutionService : IDisposable
{
    /// <summary>Gets the actual inference device after the most recent successful model run.</summary>
    VideoSrDevice? InferenceDevice { get; }
    /// <summary>Reports the actual inference accelerator, including fallback information.</summary>
    event Action<VideoSrDevice>? DeviceChanged;
    /// <summary>Gets or sets whether image enhancement should prefer a Windows ML NPU provider.</summary>
    bool PreferNpu { get; set; }
    IReadOnlyList<string> AvailableModels { get; }

    bool IsModelAvailable(SuperResolutionModel model);

    Task<SKBitmap> UpscaleAsync(
        SKBitmap source,
        SuperResolutionModel model,
        int targetScale,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    void ReleaseSessions();

    /// <summary>Installs compatible hardware providers through the Windows ML catalog.</summary>
    Task InstallAccelerationAsync(CancellationToken ct = default);

    // ── Model download state (consumed by the UI) ─────────────────────────
    Task WarmupTask { get; }

    bool IsDownloading { get; }

    string? LastDownloadError { get; }

    /// <summary>Raised on the captured SynchronizationContext for UI consumption.</summary>
    event Action<ModelDownloadProgress>? DownloadProgressChanged;

    /// <summary>Raised when <see cref="IsDownloading"/> flips, so the UI can refresh.</summary>
    event EventHandler<bool>? IsDownloadingChanged;
}
