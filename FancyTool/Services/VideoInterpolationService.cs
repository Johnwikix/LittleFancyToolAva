using FancyToolAva.Models;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

/// <summary>共享模型校验与 Windows ML 安装入口，通过独占租约复用当前插帧上下文。</summary>
public sealed class VideoInterpolationService(ModelDownloadService downloader, ILogger<VideoInterpolationService> logger) : IVideoInterpolationService, IDisposable
{
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private (string Path, int Width, int Height, VideoSrBackend Backend, bool AllowCpu, bool CudaGraph)? _cachedKey;
    private IVideoFrameInterpolator? _cached;
    private bool _disposed;
    private const string Revision = "f7bf1c91e94ef516900f68456528fa781e2e7174";
    internal static IReadOnlyList<ModelManifestEntry> Models { get; } =
    [
        Model("rife_v4.25_lite_v2.onnx", "610B5DE57CDCFBCCE9914C23E60A1CD357779A6F9582A1BCFCB035F8EB38509B"),
        Model("rife_v4.25_v2.onnx", "65C57A5E4ABB17AD67FAF35054291AC53AFFAB0506D509D2E66698F6ECD75584")
    ];
    private static ModelManifestEntry Model(string name, string hash) => new(name,
        $"https://huggingface.co/notaneimu/onnx-image-models/resolve/{Revision}/{name}",
        $"https://hf-mirror.com/notaneimu/onnx-image-models/resolve/{Revision}/{name}", hash, 1);
    private static string DirectoryPath => Path.Combine(AppPaths.ModelsDirectory, "Interpolation");
    internal static ModelManifestEntry GetModel(VideoInterpolationModel model) => Models[model switch
    { VideoInterpolationModel.Rife425Lite => 0, VideoInterpolationModel.Rife425 => 1, _ => throw new ArgumentOutOfRangeException(nameof(model)) }];

    /// <inheritdoc />
    public event Action<ModelDownloadProgress>? DownloadProgressChanged;
    /// <inheritdoc />
    public event Action<VideoSrDevice>? DeviceChanged;
    /// <inheritdoc />
    public bool IsModelAvailable(VideoInterpolationModel model) => File.Exists(Path.Combine(DirectoryPath, GetModel(model).FileName));
    /// <inheritdoc />
    public Task EnsureModelAsync(VideoInterpolationModel model, CancellationToken ct = default) => Task.Run(() =>
        downloader.UseModelsAsync([GetModel(model)], () => DirectoryPath, _ => true, DownloadProgress(), ct), ct);
    /// <inheritdoc />
    public Task InstallAccelerationAsync(CancellationToken ct = default) => WindowsMlAccelerator.InstallAsync(
        name => DownloadProgressChanged?.Invoke(new ModelDownloadProgress(name, ModelDownloadStage.Connecting, 0, null,
            LocalizationRegistry.Get("VideoTranscode.Sr_InstallingHardware") + " · " + name)), ct);

    /// <inheritdoc />
    public async Task<IVideoFrameInterpolator> CreateSessionAsync(VideoTranscodeOptions options, int width, int height, CancellationToken ct = default)
    {
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = await Task.Run(() => downloader.UseModelsAsync<IVideoFrameInterpolator>([GetModel(options.InterpolationModel)], () => DirectoryPath, root =>
            {
                string path = Path.Combine(root, GetModel(options.InterpolationModel).FileName);
                bool cudaGraph = VideoInferenceRunGuard.UseCudaGraph(options);
                var key = (path, width, height, options.InterpolationBackend, options.InterpolationAllowCpuFallback, cudaGraph);
                if (_cachedKey == key && _cached != null) return _cached;
                // 全帧 RIFE 显存随分辨率增长；只保留一个编译会话，换尺寸前先释放旧会话。
                var previous = _cached;
                _cached = null;
                _cachedKey = null;
                previous?.Dispose();
                _cached = CreateCore(options, path, width, height, cudaGraph, ct);
                _cachedKey = key;
                return _cached;
            }, DownloadProgress(), ct), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            DeviceChanged?.Invoke(session.Device);
            return new SessionLease(session, _sessionGate);
        }
        catch { _sessionGate.Release(); throw; }
    }

    private IVideoFrameInterpolator CreateCore(VideoTranscodeOptions options, string path, int width, int height, bool cudaGraph, CancellationToken ct)
    {
        var backends = options.InterpolationBackend switch
        {
            VideoSrBackend.PreferNpu => new[] { VideoSrBackend.PreferNpu, VideoSrBackend.Gpu },
            VideoSrBackend.Gpu => [VideoSrBackend.Gpu], VideoSrBackend.Cpu => [VideoSrBackend.Cpu],
            _ => [VideoSrBackend.Gpu, VideoSrBackend.PreferNpu]
        };
        var failures = new List<string>();
        foreach (var backend in backends)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (backend == VideoSrBackend.Gpu && WindowsMlAccelerator.CanUseGpuPixels(ct))
                {
                    try
                    {
                        string? reason = failures.Count == 0 ? null : string.Join("; ", failures);
                        var pixels = new OrtBgraVideoFrameInterpolator(path, width, height, ct, reason, cudaGraph);
                        return pixels;
                    }
                    catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or NotSupportedException)
                    {
                        logger.LogWarning(ex, "RIFE GPU pixel adapter unavailable; retaining the original GPU tensor path");
                    }
                }
                var session = new OrtVideoFrameInterpolator(path, backend, width, height, ct,
                    failures.Count == 0 ? null : string.Join("; ", failures));
                return session;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures.Add(ex.Message); logger.LogWarning(ex, "Interpolation backend {Backend} unavailable", backend); }
        }
        if (!options.InterpolationAllowCpuFallback)
            throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_SrHardwareFailed") + " " + string.Join("; ", failures));
        var fallback = new OrtVideoFrameInterpolator(path, VideoSrBackend.Cpu, width, height, ct, string.Join("; ", failures));
        return fallback;
    }

    /// <summary>等待在途租约归还后释放保留的 GPU 会话。</summary>
    public void Dispose()
    {
        _sessionGate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            var previous = _cached;
            _cached = null;
            _cachedKey = null;
            previous?.Dispose();
        }
        finally { _sessionGate.Release(); }
    }

    private sealed class SessionLease(IVideoFrameInterpolator inner, SemaphoreSlim gate) : IVideoFrameInterpolator
    {
        private int _released;
        public VideoSrDevice Device => inner.Device;
        public void SetFrames(SkiaSharp.SKBitmap first, SkiaSharp.SKBitmap second, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
            inner.SetFrames(first, second, ct);
        }
        public void Interpolate(double amount, SkiaSharp.SKBitmap destination, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
            inner.Interpolate(amount, destination, ct);
        }
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(); }
    }

    private IProgress<ModelDownloadProgress> DownloadProgress() => new Progress<ModelDownloadProgress>(p => DownloadProgressChanged?.Invoke(p));
}
