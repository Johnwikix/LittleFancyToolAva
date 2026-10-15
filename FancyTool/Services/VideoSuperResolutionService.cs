using FancyToolAva.Models;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

/// <summary>Shares the image model installer while giving every video a private, serial hardware inference session.</summary>
public sealed class VideoSuperResolutionService(ModelDownloadService downloader, ILogger<VideoSuperResolutionService> logger, AppPreferences? preferences = null) : IVideoSuperResolutionService
{
    /// <inheritdoc />
    public event Action<ModelDownloadProgress>? DownloadProgressChanged;
    /// <inheritdoc />
    public event Action<VideoSrDevice>? DeviceChanged;

    internal static ModelManifestEntry GetModel(SuperResolutionModel model) => ModelManifest.RealEsrgan[(int)model];

    /// <inheritdoc />
    public bool IsModelAvailable(SuperResolutionModel model) => File.Exists(Path.Combine(AppPaths.ModelsDirectory, GetModel(model).FileName));

    /// <inheritdoc />
    public Task EnsureModelAsync(SuperResolutionModel model, CancellationToken ct = default)
        => Task.Run(() => downloader.UseModelsAsync([GetModel(model)], () => AppPaths.ModelsDirectory, _ => true,
            new Progress<ModelDownloadProgress>(p => DownloadProgressChanged?.Invoke(p)), ct), ct);

    /// <inheritdoc />
    public Task InstallAccelerationAsync(CancellationToken ct = default) => WindowsMlAccelerator.InstallAsync(
        name => DownloadProgressChanged?.Invoke(new ModelDownloadProgress(name, ModelDownloadStage.Connecting, 0, null, LocalizationRegistry.Get("VideoTranscode.Sr_InstallingHardware") + " · " + name)), ct);

    /// <inheritdoc />
    public async Task<IVideoFrameUpscaler> CreateSessionAsync(VideoTranscodeOptions options, CancellationToken ct = default)
    {
        if (options.SuperResolutionScale is not (2 or 4)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.SuperResolutionScale > GetModel(options.SuperResolutionModel).NativeScale) throw new ArgumentOutOfRangeException(nameof(options), "This model supports 2× output only");
        return await Task.Run(() => downloader.UseModelsAsync<IVideoFrameUpscaler>([GetModel(options.SuperResolutionModel)],
            () => AppPaths.ModelsDirectory, directory =>
        {
            string path = Path.Combine(directory, GetModel(options.SuperResolutionModel).FileName);
            int nativeScale = GetModel(options.SuperResolutionModel).NativeScale;
            int window = new[] { 192, 288, 384, 576 }[Math.Clamp(preferences?.SuperResolutionTileSizeIndex ?? 2, 0, 3)];
            var backends = options.SuperResolutionBackend switch
            {
                VideoSrBackend.PreferNpu => new[] { VideoSrBackend.PreferNpu, VideoSrBackend.Gpu },
                VideoSrBackend.Gpu => new[] { VideoSrBackend.Gpu },
                VideoSrBackend.Cpu => new[] { VideoSrBackend.Cpu },
                _ => new[] { VideoSrBackend.Gpu, VideoSrBackend.PreferNpu }
            };
            var failures = new List<string>();
            foreach (var backend in backends)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var tile = SrTileFactory.Create(path, backend, window, ct, out var selected, nativeScale);
                    var device = selected with { FallbackReason = failures.Count > 0 ? string.Join("; ", failures) : selected.FallbackReason };
                    DeviceChanged?.Invoke(device);
                    logger.LogInformation("Video SR inference device: {Device}; fallback: {Fallback}", device.Name, device.FallbackReason);
                    return new VideoFrameUpscaler(tile, device, window, nativeScale);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures.Add(ex.Message); logger.LogWarning(ex, "Video SR backend {Backend} unavailable", backend); }
            }
            if (!options.SuperResolutionAllowCpuFallback)
                throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_SrHardwareFailed") + " " + string.Join("; ", failures));
            var fallback = new VideoSrDevice("CPU", false, string.Join("; ", failures));
            var cpu = new OrtVideoSrTileModel(path, VideoSrBackend.Cpu, window, ct, nativeScale);
            DeviceChanged?.Invoke(fallback);
            return new VideoFrameUpscaler(cpu, fallback, window, nativeScale);
        }, new Progress<ModelDownloadProgress>(p => DownloadProgressChanged?.Invoke(p)), ct), ct).ConfigureAwait(false);
    }
}
