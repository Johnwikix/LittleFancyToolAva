using FancyToolAva.Models;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

/// <summary>Shares the image model installer while giving every video a private, serial hardware inference session.</summary>
public sealed class VideoSuperResolutionService(ModelDownloadService downloader, ILogger<VideoSuperResolutionService> logger) : IVideoSuperResolutionService
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
        => Task.Run(() => downloader.EnsureModelsAsync([GetModel(model)], AppPaths.ModelsDirectory,
            new Progress<ModelDownloadProgress>(p => DownloadProgressChanged?.Invoke(p)), ct), ct);

    /// <inheritdoc />
    public async Task<IVideoFrameUpscaler> CreateSessionAsync(VideoTranscodeOptions options, CancellationToken ct = default)
    {
        if (options.SuperResolutionScale is not (2 or 4)) throw new ArgumentOutOfRangeException(nameof(options));
        await EnsureModelAsync(options.SuperResolutionModel, ct).ConfigureAwait(false);
        return await Task.Run<IVideoFrameUpscaler>(() =>
        {
            string path = Path.Combine(AppPaths.ModelsDirectory, GetModel(options.SuperResolutionModel).FileName);
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
                    IVideoSrTileModel tile;
                    string name;
                    if (backend == VideoSrBackend.PreferNpu)
                    { tile = new NpuSrTileModel(path, ct); name = "NPU · Intel AI Boost"; }
                    else if (backend == VideoSrBackend.Gpu)
                    {
                        var gpu = VideoSrGpuSelector.Select();
                        tile = new OrtVideoSrTileModel(path, gpu.Index);
                        name = "GPU · " + gpu.Name;
                    }
                    else { tile = new OrtVideoSrTileModel(path, null); name = "CPU"; }
                    var device = new VideoSrDevice(name, backend != VideoSrBackend.Cpu, failures.Count > 0 ? string.Join("; ", failures) : null);
                    DeviceChanged?.Invoke(device);
                    logger.LogInformation("Video SR inference device: {Device}; fallback: {Fallback}", name, device.FallbackReason);
                    return new VideoFrameUpscaler(tile, device);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures.Add(ex.Message); logger.LogWarning(ex, "Video SR backend {Backend} unavailable", backend); }
            }
            if (!options.SuperResolutionAllowCpuFallback)
                throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_SrHardwareFailed") + " " + string.Join("; ", failures));
            var fallback = new VideoSrDevice("CPU", false, string.Join("; ", failures));
            var cpu = new OrtVideoSrTileModel(path, null);
            DeviceChanged?.Invoke(fallback);
            return new VideoFrameUpscaler(cpu, fallback);
        }, ct).ConfigureAwait(false);
    }
}
