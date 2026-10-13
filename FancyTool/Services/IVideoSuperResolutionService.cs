using FancyToolAva.Models;
using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>Specifies the preferred video super-resolution inference backend.</summary>
public enum VideoSrBackend
{
    /// <summary>Prefers a vendor GPU provider or DirectML, then an available NPU.</summary>
    Auto,
    /// <summary>Prefers an available Windows ML NPU provider, then an available GPU.</summary>
    PreferNpu,
    /// <summary>Uses a vendor GPU provider, with DirectML hardware fallback.</summary>
    Gpu,
    /// <summary>Uses software inference.</summary>
    Cpu
}

/// <summary>Reports the actual accelerator and any hardware fallback reason.</summary>
public sealed record VideoSrDevice(string Name, bool IsHardware, string? FallbackReason = null);

/// <summary>Owns a serial inference session reused for all frames of one video.</summary>
public interface IVideoFrameUpscaler : IDisposable
{
    /// <summary>Gets the actual inference device.</summary>
    VideoSrDevice Device { get; }
    /// <summary>Enhances an opaque BGRA frame and returns a bitmap owned by the caller.</summary>
    /// <param name="source">The source frame.</param>
    /// <param name="scale">The output scale, two or four.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The enhanced frame.</returns>
    SKBitmap Upscale(SKBitmap source, int scale, CancellationToken ct);
}

/// <summary>Installs shared image/video models and creates hardware-preferred video inference sessions.</summary>
public interface IVideoSuperResolutionService
{
    /// <summary>Reports model download and validation progress.</summary>
    event Action<ModelDownloadProgress>? DownloadProgressChanged;
    /// <summary>Reports the accelerator selected after session creation.</summary>
    event Action<VideoSrDevice>? DeviceChanged;
    /// <summary>Gets whether the selected model exists in the shared model directory.</summary>
    bool IsModelAvailable(SuperResolutionModel model);
    /// <summary>Downloads and validates the selected model using the image model installer.</summary>
    Task EnsureModelAsync(SuperResolutionModel model, CancellationToken ct = default);
    /// <summary>Installs compatible GPU/NPU providers through the Windows ML catalog.</summary>
    Task InstallAccelerationAsync(CancellationToken ct = default);
    /// <summary>Creates a serial inference session for the selected model and hardware preference.</summary>
    Task<IVideoFrameUpscaler> CreateSessionAsync(VideoTranscodeOptions options, CancellationToken ct = default);
}
