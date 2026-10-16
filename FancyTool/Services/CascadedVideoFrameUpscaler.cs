using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>Reuses a native 2× GPU session for one-pass 2× or two-pass 4× output.</summary>
internal sealed class CascadedVideoFrameUpscaler(IVideoFrameUpscaler inner) : IVideoFrameUpscaler
{
    /// <inheritdoc />
    public VideoSrDevice Device => inner.Device;

    /// <inheritdoc />
    public SKBitmap Upscale(SKBitmap source, int scale, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        VideoFrameUpscaler.ValidateSize(source.Width, source.Height, scale);
        if (scale == 2) return inner.Upscale(source, 2, ct);
        using var intermediate = inner.Upscale(source, 2, ct);
        ct.ThrowIfCancellationRequested();
        return inner.Upscale(intermediate, 2, ct);
    }

    /// <inheritdoc />
    public void Dispose() => inner.Dispose();
}
