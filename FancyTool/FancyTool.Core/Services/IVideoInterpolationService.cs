using FancyToolAva.Models;
using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>Specifies how intermediate video frames are generated.</summary>
public enum VideoInterpolationMode
{
    /// <summary>Uses ordinary frame rate conversion.</summary>
    None,
    /// <summary>Generates intermediate frames using RIFE.</summary>
    Rife,
    /// <summary>Uses FFmpeg motion estimation and compensation on the CPU.</summary>
    MotionCompensation,
    /// <summary>Uses AMD AMF GPU motion estimation and frame rate conversion.</summary>
    AmdOpticalFlow,
    /// <summary>Uses NVIDIA optical flow hardware through Vulkan.</summary>
    NvidiaOpticalFlow
}

/// <summary>Specifies the RIFE model used for frame interpolation.</summary>
public enum VideoInterpolationModel
{
    /// <summary>Uses Practical-RIFE 4.25 Lite.</summary>
    Rife425Lite,
    /// <summary>Uses Practical-RIFE 4.25.</summary>
    Rife425
}

/// <summary>Specifies the ordering of neural frame generation and image enhancement.</summary>
public enum VideoFrameProcessingOrder
{
    /// <summary>Enhances first for modest AI frame sizes and otherwise interpolates first.</summary>
    Auto,
    /// <summary>Interpolates at source resolution before enhancing all output frames.</summary>
    InterpolationThenUpscale,
    /// <summary>Enhances original frames before interpolating at the larger resolution.</summary>
    UpscaleThenInterpolation
}

/// <summary>Owns a serial interpolation session for one frame size.</summary>
public interface IVideoFrameInterpolator : IDisposable
{
    /// <summary>Gets the actual inference device and hardware fallback reason.</summary>
    VideoSrDevice Device { get; }
    /// <summary>Prepares consecutive opaque BGRA frames for repeated temporal sampling.</summary>
    void SetFrames(SKBitmap first, SKBitmap second, CancellationToken ct);
    /// <summary>Writes an intermediate frame at a time strictly between zero and one.</summary>
    void Interpolate(double amount, SKBitmap destination, CancellationToken ct);
}

/// <summary>Installs interpolation models and creates sessions through Windows ML.</summary>
public interface IVideoInterpolationService
{
    /// <summary>Reports model download and validation progress.</summary>
    event Action<ModelDownloadProgress>? DownloadProgressChanged;
    /// <summary>Reports the selected inference accelerator.</summary>
    event Action<VideoSrDevice>? DeviceChanged;
    /// <summary>Gets whether the selected model exists in the active model directory.</summary>
    bool IsModelAvailable(VideoInterpolationModel model);
    /// <summary>Downloads and verifies the selected model.</summary>
    Task EnsureModelAsync(VideoInterpolationModel model, CancellationToken ct = default);
    /// <summary>Installs compatible hardware providers from the Windows ML catalog.</summary>
    Task InstallAccelerationAsync(CancellationToken ct = default);
    /// <summary>Creates an interpolation session specialized for the given video dimensions.</summary>
    Task<IVideoFrameInterpolator> CreateSessionAsync(VideoTranscodeOptions options, int width, int height, CancellationToken ct = default);
}
