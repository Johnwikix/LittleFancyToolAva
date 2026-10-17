using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>每个并行块独占原生会话和张量，帧返回前等待所有块完成。</summary>
internal sealed class ParallelBgraVideoFrameUpscaler : IVideoFrameUpscaler, IVideoFrameDestinationUpscaler, IVideoInferenceSessions
{
    private readonly int _window, _degree;
    private readonly List<OrtBgraVideoFrameUpscaler> _contexts;
    private readonly BlockingCollection<OrtBgraVideoFrameUpscaler> _available;
    private bool _disposed;
    public VideoSrDevice Device => _contexts[0].Device;
    public int Count => _contexts.Count;
    public int Reserved => _degree;

    internal ParallelBgraVideoFrameUpscaler(string path, int window, bool half, int degree, ILogger logger, CancellationToken ct, VideoSrBackend backend = VideoSrBackend.VendorGpu)
    {
        _window = window;
        _degree = degree;
        _contexts = new(degree);
        _available = new(degree);
        try
        {
            for (int i = 0; i < degree; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var context = new OrtBgraVideoFrameUpscaler(path, window, half, ct, useCudaGraph: false, backend: backend);
                    _contexts.Add(context);
                    _available.Add(context);
                }
                catch (Exception error) when (_contexts.Count > 0 && error is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or NotSupportedException)
                {
                    logger.LogWarning(error, "Video SR retained {Count} of {Requested} GPU sessions", _contexts.Count, degree);
                    break;
                }
            }
            logger.LogInformation("Video SR uses {Count} independent GPU sessions", _contexts.Count);
        }
        catch
        {
            try { Dispose(); } catch (Exception cleanup) { System.Diagnostics.Debug.WriteLine(cleanup); }
            throw;
        }
    }

    public SKBitmap Upscale(SKBitmap source, int scale, CancellationToken ct)
    {
        VideoFrameUpscaler.ValidateSize(source.Width, source.Height, scale);
        var result = new SKBitmap(source.Width * scale, source.Height * scale, SKColorType.Bgra8888, SKAlphaType.Opaque);
        try { TryUpscaleInto(source, scale, result, ct); return result; }
        catch { result.Dispose(); throw; }
    }

    public bool TryUpscaleInto(SKBitmap source, int scale, SKBitmap destination, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrameUpscaler.ValidateSize(source.Width, source.Height, scale);
        if (scale != 2 || destination.Width != source.Width * 2 || destination.Height != source.Height * 2 || destination.ColorType != SKColorType.Bgra8888)
            throw new ArgumentException("Invalid destination bitmap");
        int tile = _window - 64, columns = (source.Width + tile - 1) / tile;
        int total = columns * ((source.Height + tile - 1) / tile);
        if (total < 8) return _contexts[0].TryUpscaleInto(source, scale, destination, ct);
        if (_contexts.Count == 1) return _contexts[0].TryUpscaleInto(source, scale, destination, ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ExceptionDispatchInfo? failure = null;
        var metrics = VideoPipelineMetrics.Current;
        try
        {
            Parallel.For(0, total, new ParallelOptions { MaxDegreeOfParallelism = _contexts.Count, CancellationToken = stop.Token },
                () =>
                {
                    var context = _available.Take(stop.Token);
                    var previous = VideoPipelineMetrics.Current;
                    VideoPipelineMetrics.Current = metrics;
                    return (Context: context, Previous: previous);
                }, (index, state, worker) =>
                {
                    try { worker.Context.ProcessTile(source, destination, index % columns * tile, index / columns * tile, stop.Token); }
                    catch (Exception error)
                    {
                        if (error is not OperationCanceledException || !stop.IsCancellationRequested)
                            Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(error), null);
                        try { stop.Cancel(); }
                        catch (Exception cancellation) { Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(cancellation), null); }
                        throw;
                    }
                    return worker;
                }, worker =>
                {
                    VideoPipelineMetrics.Current = worker.Previous;
                    _available.Add(worker.Context);
                });
        }
        catch
        {
            failure?.Throw();
            ct.ThrowIfCancellationRequested();
            throw;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ExceptionDispatchInfo? failure = null;
        foreach (var context in _contexts)
            try { context.Dispose(); }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        _contexts.Clear();
        _available.Dispose();
        failure?.Throw();
    }
}
