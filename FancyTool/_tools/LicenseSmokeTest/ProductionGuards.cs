using System.Net;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace LicenseSmokeTest;

internal static partial class Program
{
    private static async Task ProductionGuardsAsync(string workDirectory)
    {
        using var dispatcher = new TestDispatcher();
        var backend = new StoreProbe();
        using var license = new LicenseService(backend, new TestTimeProvider(Epoch), dispatcher);
        using var requests = new DeniedDownloadProbe();
        using var http = new HttpClient(requests);
        var downloader = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
        using var image = new SuperResolutionService(NullLogger<SuperResolutionService>.Instance, new AppPreferences(), downloader, license);
        using var video = new VideoSuperResolutionService(downloader, NullLogger<VideoSuperResolutionService>.Instance, licenseService: license);
        using var interpolation = new VideoInterpolationService(downloader, NullLogger<VideoInterpolationService>.Instance, license);
        var transcode = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance, video, interpolation, license);

        // Store 初查未完成时，页面构造不能触发模型目录创建、下载或原生会话。
        await image.WarmupTask;
        Check(requests.Calls == 0 && !image.IsDownloading && !Directory.Exists(AppPaths.ModelsDirectory),
            "许可尚未初始化时图片服务提前预热。");
        string missingInput = Path.Combine(workDirectory, "missing-input.mp4");
        string outputDirectory = Path.Combine(workDirectory, "must-not-create");
        string outputPath = Path.Combine(outputDirectory, "output.mp4");
        var blockedBeforeInit = transcode.TranscodeAsync(missingInput, outputPath, new VideoTranscodeOptions { SuperResolutionEnabled = true });
        await dispatcher.DrainAsync();
        Check(!blockedBeforeInit.IsCompleted, "AI 转码没有等待初始许可确认。");

        // 普通转码及非 AI 插帧必须绕过许可等待；不存在的输入在 FFmpeg 加载之前失败。
        foreach (var mode in Enum.GetValues<VideoInterpolationMode>().Where(mode => mode != VideoInterpolationMode.Rife))
            await ThrowsAsync<FileNotFoundException>(() => transcode.TranscodeAsync(missingInput, outputPath,
                new VideoTranscodeOptions { InterpolationMode = mode }).WaitAsync(TimeSpan.FromSeconds(1)));

        await license.InitializeAsync((nint)1234);
        await ThrowsAsync<LicenseRequiredException>(() => blockedBeforeInit);
        await image.WarmupTask;
        Check(requests.Calls == 0 && !image.IsDownloading, "受限图片服务启动了自动下载。");
        using var bitmap = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Opaque);
        int deviceEvents = 0;
        image.DeviceChanged += _ => Interlocked.Increment(ref deviceEvents);
        video.DeviceChanged += _ => Interlocked.Increment(ref deviceEvents);
        interpolation.DeviceChanged += _ => Interlocked.Increment(ref deviceEvents);

        await ThrowsAsync<LicenseRequiredException>(() => image.UpscaleAsync(bitmap, SuperResolutionModel.SpanClearRealityV1, 2));
        await ThrowsAsync<LicenseRequiredException>(() => image.InstallAccelerationAsync());
        await ThrowsAsync<LicenseRequiredException>(() => video.EnsureModelAsync(VideoSrModel.Anime4KCnnS));
        await ThrowsAsync<LicenseRequiredException>(() => video.InstallAccelerationAsync());
        await ThrowsAsync<LicenseRequiredException>(() => video.CreateSessionAsync(new VideoTranscodeOptions { SuperResolutionEnabled = true }));
        await ThrowsAsync<LicenseRequiredException>(() => interpolation.EnsureModelAsync(VideoInterpolationModel.Rife425Lite));
        await ThrowsAsync<LicenseRequiredException>(() => interpolation.InstallAccelerationAsync());
        await ThrowsAsync<LicenseRequiredException>(() => interpolation.CreateSessionAsync(new VideoTranscodeOptions
        { InterpolationMode = VideoInterpolationMode.Rife }, 32, 32));

        // 入口必须先门控；即使输入不存在也应得到许可错误，不能降级到普通转码。
        foreach (var options in new[]
        {
            new VideoTranscodeOptions { SuperResolutionEnabled = true },
            new VideoTranscodeOptions { InterpolationMode = VideoInterpolationMode.Rife },
            new VideoTranscodeOptions { SuperResolutionEnabled = true, InterpolationMode = VideoInterpolationMode.Rife }
        })
            await ThrowsAsync<LicenseRequiredException>(() => transcode.TranscodeAsync(missingInput, outputPath, options));

        Check(requests.Calls == 0 && deviceEvents == 0 && !Directory.Exists(outputDirectory)
            && !Directory.Exists(AppPaths.ModelsDirectory), "AI 入口拒绝之前已产生下载、设备会话、模型目录或输出。");

        // 使用真实状态机恢复许可，验证现有服务沿真实下载器启动一次预热；离线边界返回 404。
        backend.Current = new(true, false, null);
        await license.RefreshAsync();
        await image.WarmupTask;
        Check(requests.Calls > 0 && image.LastDownloadError != null && !image.IsDownloading,
            "授权恢复后图片服务未启动一次真实预热，或离线失败未收尾。");
        int warmedRequests = requests.Calls;
        await license.RefreshAsync();
        await image.WarmupTask;
        Check(requests.Calls == warmedRequests, "重复许可通知启动了重复预热。");
    }

    private sealed class DeniedDownloadProbe : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
