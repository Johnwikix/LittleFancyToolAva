using Microsoft.ML.OnnxRuntime;
using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>GPU 完成 RIFE 的通道归一化、时间平面展开和输出量化。</summary>
internal sealed class OrtBgraVideoFrameInterpolator : IVideoFrameInterpolator
{
    private readonly VideoGpuPixelSession _gpu;
    private readonly int _width, _height, _inputWidth, _inputHeight;
    private bool _disposed;
    public VideoSrDevice Device { get; }

    internal OrtBgraVideoFrameInterpolator(string path, int width, int height, CancellationToken ct, string? fallbackReason, bool useCudaGraph = true)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 3840L * 2160)
            throw new ArgumentOutOfRangeException(nameof(width), LocalizationRegistry.Get("VideoTranscode.Interp_SizeLimit"));
        _width = width; _height = height;
        _inputWidth = width <= 64 ? 128 : width;
        _inputHeight = height <= 64 ? 128 : height;
        if ((long)_inputWidth * _inputHeight > 3840L * 2160)
            throw new ArgumentOutOfRangeException(nameof(width), LocalizationRegistry.Get("VideoTranscode.Interp_SizeLimit"));
        _gpu = new VideoGpuPixelSession(path, _inputWidth, _inputHeight, useCudaGraph, ct);
        Device = _gpu.Device with { FallbackReason = fallbackReason ?? _gpu.Device.FallbackReason };
    }

    public void SetFrames(SKBitmap first, SKBitmap second, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Validate(first); Validate(second);
        long stamp = VideoPipelineMetrics.Start();
        VideoBgraPixels.CopyPadded(first, 0, 0, _inputWidth, _inputHeight, 0, _gpu.First, ct);
        VideoBgraPixels.CopyPadded(second, 0, 0, _inputWidth, _inputHeight, 0, _gpu.Second, ct);
        _gpu.FramesChanged();
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.RifePrepare, stamp);
    }

    public void Interpolate(double amount, SKBitmap destination, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Validate(destination);
        if (!double.IsFinite(amount) || amount <= 0 || amount >= 1) throw new ArgumentOutOfRangeException(nameof(amount));
        RenderSample((float)amount, ct);
        CopySample(destination, ct);
    }

    internal void RenderSample(float amount, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(amount) || amount < 0 || amount > 1) throw new ArgumentOutOfRangeException(nameof(amount));
        ct.ThrowIfCancellationRequested();
        _gpu.SetAmount((float)amount);
        try
        {
            long stamp = VideoPipelineMetrics.Start();
            _gpu.Run(ct);
            VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.RifeRun, stamp);
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    internal void CopySample(SKBitmap destination, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Validate(destination);
        long stamp = VideoPipelineMetrics.Start();
        VideoBgraPixels.Paste(_gpu.Output, _inputWidth * 4, destination, 0, 0, _width, _height, ct);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.RifeReadback, stamp);
    }

    private void Validate(SKBitmap bitmap)
    {
        if (bitmap.Width != _width || bitmap.Height != _height || bitmap.ColorType != SKColorType.Bgra8888)
            throw new ArgumentException(LocalizationRegistry.Get("VideoTranscode.Interp_InvalidFrames"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gpu.Dispose();
    }
}
