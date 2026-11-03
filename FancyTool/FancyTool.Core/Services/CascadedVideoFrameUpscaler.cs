using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>Reuses a native 2× GPU session for one-pass 2× or two-pass 4× output.</summary>
internal sealed class CascadedVideoFrameUpscaler(IVideoFrameUpscaler inner) : IVideoFrameUpscaler, IVideoFrameDestinationUpscaler, IVideoCudaUpscaler, IVideoInferenceSessions
{
    private CudaVideoContext? _cuda;
    private CudaVideoContext.CudaBuffer? _cudaIntermediate;

    public bool SupportsCuda => inner is IVideoCudaUpscaler { SupportsCuda: true };
    public void UpscaleCuda(CudaVideoFrame source, CudaVideoFrame destination, int scale, CancellationToken ct)
    {
        if (inner is not IVideoCudaUpscaler { SupportsCuda: true } gpu) throw new NotSupportedException();
        ct.ThrowIfCancellationRequested();
        if (scale == 2) { gpu.UpscaleCuda(source, destination, 2, ct); return; }
        if (scale != 4) throw new ArgumentOutOfRangeException(nameof(scale));
        VideoFrameUpscaler.ValidateSize(source.Width, source.Height, scale);
        _cuda ??= new CudaVideoContext(Device.TensorRtDeviceId!.Value);
        if (_cudaIntermediate == null || _cudaIntermediate.Frame.Width != source.Width * 2 || _cudaIntermediate.Frame.Height != source.Height * 2)
        {
            _cudaIntermediate?.Dispose();
            _cudaIntermediate = null;
            _cudaIntermediate = _cuda.Allocate(source.Width * 2, source.Height * 2);
        }
        gpu.UpscaleCuda(source, _cudaIntermediate.Frame, 2, ct);
        gpu.UpscaleCuda(_cudaIntermediate.Frame, destination, 2, ct);
    }
    public int Count => (inner as IVideoInferenceSessions)?.Count ?? 1;
    public int Reserved => (inner as IVideoInferenceSessions)?.Reserved ?? 1;
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
    public void Dispose()
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        void Release(IDisposable? value)
        {
            try { value?.Dispose(); }
            catch (Exception error) { failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
        }
        Release(_cudaIntermediate);
        _cudaIntermediate = null;
        Release(_cuda);
        _cuda = null;
        Release(inner);
        failure?.Throw();
    }

    public bool TryUpscaleInto(SKBitmap source, int scale, SKBitmap destination, CancellationToken ct)
    {
        if (inner is not IVideoFrameDestinationUpscaler writer) return false;
        ct.ThrowIfCancellationRequested();
        VideoFrameUpscaler.ValidateSize(source.Width, source.Height, scale);
        if (scale == 2) return writer.TryUpscaleInto(source, 2, destination, ct);
        using var intermediate = inner.Upscale(source, 2, ct);
        return writer.TryUpscaleInto(intermediate, 2, destination, ct);
    }
}
