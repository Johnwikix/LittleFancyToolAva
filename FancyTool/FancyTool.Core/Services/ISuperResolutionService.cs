using SkiaSharp;

namespace FancyToolAva.Services;

public enum SuperResolutionModel
{
    RealEsrganX4Plus = 0,
    RealEsrganX4PlusAnime = 1,
    RealEsrganGeneralX4V3 = 2,
    // 编号用于历史页面状态；移除模型后保留空位，避免旧设置指向其他模型。
    SpanClearRealityV1 = 4,
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
