using System.Runtime.ExceptionServices;
using FancyToolAva.Models;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

/// <summary>Installs packaged models and leases reusable GPU-only sessions exclusively to each video.</summary>
public sealed class VideoSuperResolutionService(ModelDownloadService downloader, ILogger<VideoSuperResolutionService> logger, AppPreferences? preferences = null) : IVideoSuperResolutionService, IDisposable
{
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly Dictionary<(string Path, int Window, VideoSrBackend Backend, bool Half, bool CudaGraph, int Degree), IVideoFrameUpscaler> _sessions = new();
    private volatile bool _disposed;
    /// <inheritdoc />
    public event Action<ModelDownloadProgress>? DownloadProgressChanged;
    /// <inheritdoc />
    public event Action<VideoSrDevice>? DeviceChanged;

    /// <inheritdoc />
    public bool IsModelAvailable(VideoSrModel model) => File.Exists(Path.Combine(AppPaths.ModelsDirectory, "VideoSr", VideoSrModelCatalog.Get(model).FullPrecision.FileName));

    /// <inheritdoc />
    public Task EnsureModelAsync(VideoSrModel model, CancellationToken ct = default)
    {
        var definition = VideoSrModelCatalog.Get(model);
        ModelManifestEntry[] graphs = definition.HalfPrecision is { } half ? [definition.FullPrecision, half] : [definition.FullPrecision];
        return Task.Run(() => downloader.UseModelsAsync(graphs, () => Path.Combine(AppPaths.ModelsDirectory, "VideoSr"), _ => true,
            new Progress<ModelDownloadProgress>(p => DownloadProgressChanged?.Invoke(p)), ct), ct);
    }

    /// <inheritdoc />
    public Task InstallAccelerationAsync(CancellationToken ct = default) => WindowsMlAccelerator.InstallAsync(
        name => DownloadProgressChanged?.Invoke(new ModelDownloadProgress(name, ModelDownloadStage.Connecting, 0, null,
            LocalizationRegistry.Get("VideoTranscode.Sr_InstallingHardware") + " · " + name)), ct);

    /// <inheritdoc />
    public async Task<IVideoFrameUpscaler> CreateSessionAsync(VideoTranscodeOptions options, CancellationToken ct = default)
    {
        if (options.SuperResolutionScale is not (2 or 4)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.SuperResolutionBackend is not (VideoSrBackend.Auto or VideoSrBackend.Gpu or VideoSrBackend.DirectMl))
            throw new NotSupportedException(LocalizationRegistry.Get("VideoTranscode.Sr_GpuRequired"));
        var definition = VideoSrModelCatalog.Get(options.SuperResolutionModel);
        var graph = definition.GetGraph(options.SuperResolutionUseFp16);
        if (!definition.RequiresDirectMl && options.SuperResolutionBackend == VideoSrBackend.DirectMl)
            throw new NotSupportedException(LocalizationRegistry.Get("VideoTranscode.Sr_VendorGpuRequired"));
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = await downloader.UseModelsAsync<IVideoFrameUpscaler>([graph],
                () => Path.Combine(AppPaths.ModelsDirectory, "VideoSr"), directory =>
            {
                ct.ThrowIfCancellationRequested();
                int window = new[] { 192, 288, 384, 576 }[Math.Clamp(preferences?.SuperResolutionTileSizeIndex ?? 2, 0, 3)];
                if (definition.RequiresDirectMl) window = Math.Max(window, 384);
                var backend = definition.RequiresDirectMl ? VideoSrBackend.DirectMl : VideoSrBackend.VendorGpu;
                bool gpuPixels = definition.RequiresDirectMl || WindowsMlAccelerator.CanUseGpuPixels(ct);
                int degree = gpuPixels ? GpuParallelism(definition.RequiresDirectMl) : 1;
                bool cudaGraph = degree == 1 && VideoInferenceRunGuard.UseCudaGraph(options);
                var key = (Path.Combine(directory, graph.FileName), window, backend, options.SuperResolutionUseFp16, cudaGraph, degree);
                if (_sessions.TryGetValue(key, out var cached)) return cached;
                // Bound retained compiled graphs; leases serialize native inference and eviction.
                while (_sessions.Count > 0 && _sessions.Values.Sum(s => (s as IVideoInferenceSessions)?.Reserved ?? 1) + degree > 10)
                {
                    var oldest = _sessions.First();
                    _sessions.Remove(oldest.Key);
                    oldest.Value.Dispose();
                }
                if (gpuPixels)
                {
                    try
                    {
                        IVideoFrameUpscaler runner = degree > 1
                            ? new ParallelBgraVideoFrameUpscaler(Path.Combine(directory, graph.FileName), window, options.SuperResolutionUseFp16, degree, logger, ct, backend)
                            : new OrtBgraVideoFrameUpscaler(Path.Combine(directory, graph.FileName), window, options.SuperResolutionUseFp16, ct, cudaGraph, backend);
                        var pixels = new CascadedVideoFrameUpscaler(runner);
                        _sessions.Add(key, pixels);
                        return pixels;
                    }
                    catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or NotSupportedException)
                    {
                        logger.LogWarning(ex, "Video SR GPU pixel adapter unavailable; retaining the original GPU tensor path");
                    }
                }
                var tile = new OrtVideoSrTileModel(Path.Combine(directory, graph.FileName), backend, window, ct, 2,
                    requireHardware: true, useFp16: options.SuperResolutionUseFp16);
                try
                {
                    var upscaler = new CascadedVideoFrameUpscaler(new VideoFrameUpscaler(tile, tile.Device, window, 2));
                    _sessions.Add(key, upscaler);
                    return upscaler;
                }
                catch { tile.Dispose(); throw; }
            }, new Progress<ModelDownloadProgress>(p => DownloadProgressChanged?.Invoke(p)), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            DeviceChanged?.Invoke(session.Device);
            logger.LogInformation("Video SR {Model}, FP16={Fp16}, device={Device}", definition.Model, options.SuperResolutionUseFp16, session.Device.Name);
            return new SessionLease(session, _sessionGate);
        }
        catch { _sessionGate.Release(); throw; }
    }

    private static int GpuParallelism(bool directMl)
    {
        // 诊断可覆盖并发数；默认根据独立显存与 CPU 核心数限制工作集。
        if (int.TryParse(Environment.GetEnvironmentVariable("FANCYTOOL_VIDEO_SR_WORKERS"), out int requested))
            return Math.Clamp(requested, 1, 8);
        ulong memory = (ulong)(VideoSrGpuSelector.EnumerateAdapters().Where(d => d.VendorId == 0x10de)
            .Select(d => d.VideoMemory).DefaultIfEmpty().Min());
        int degree = memory >= 12UL * 1024 * 1024 * 1024 ? 4 : memory >= 8UL * 1024 * 1024 * 1024 ? 2 : 1;
        if (directMl) degree = Math.Min(degree, 2);
        return Math.Min(degree, Math.Max(1, Environment.ProcessorCount / 4));
    }

    /// <summary>Disposes retained GPU sessions after all outstanding video work completes.</summary>
    public void Dispose()
    {
        _sessionGate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            ExceptionDispatchInfo? failure = null;
            foreach (var session in _sessions.Values)
            {
                try { session.Dispose(); }
                catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
            }
            _sessions.Clear();
            failure?.Throw();
        }
        finally { _sessionGate.Release(); }
    }

    private sealed class SessionLease(IVideoFrameUpscaler inner, SemaphoreSlim gate) : IVideoFrameUpscaler, IVideoFrameDestinationUpscaler, IVideoInferenceSessions, IVideoFrameReuseUpscaler
    {
        private int _released;
        /// <inheritdoc />
        public VideoSrDevice Device => inner.Device;
        public int Count => (inner as IVideoInferenceSessions)?.Count ?? 1;
        public int Reserved => (inner as IVideoInferenceSessions)?.Reserved ?? 1;
        /// <inheritdoc />
        public SkiaSharp.SKBitmap Upscale(SkiaSharp.SKBitmap source, int scale, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
            return inner.Upscale(source, scale, ct);
        }
        /// <inheritdoc />
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(); }

        public bool TryUpscaleInto(SkiaSharp.SKBitmap source, int scale, SkiaSharp.SKBitmap destination, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
            return inner is IVideoFrameDestinationUpscaler writer && writer.TryUpscaleInto(source, scale, destination, ct);
        }
    }
}
