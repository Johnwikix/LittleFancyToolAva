using System.Reflection;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace LicenseSmokeTest;

internal static partial class Program
{
    private static async Task QueuedGuardsAsync()
    {
        using var dispatcher = new TestDispatcher();
        var backend = new StoreProbe();
        using var license = new LicenseService(backend, new TestTimeProvider(Epoch), dispatcher);
        await license.InitializeAsync((nint)1234);
        using var requests = new DeniedDownloadProbe();
        using var http = new HttpClient(requests);
        var downloader = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
        using var image = new SuperResolutionService(NullLogger<SuperResolutionService>.Instance, new AppPreferences(), downloader, license);
        using var video = new VideoSuperResolutionService(downloader, NullLogger<VideoSuperResolutionService>.Instance, licenseService: license);
        using var interpolation = new VideoInterpolationService(downloader, NullLogger<VideoInterpolationService>.Instance, license);

        backend.Current = new(true, false, null);
        await license.RefreshAsync();
        await image.WarmupTask;
        int beforeRequests = requests.Calls;
        Directory.CreateDirectory(AppPaths.ModelsDirectory);
        // 仅满足文件存在性检查；排队后必须在读取这份伪图前拒绝，绝不能加载原生模型。
        File.WriteAllText(Path.Combine(AppPaths.ModelsDirectory, ModelManifest.GetImageModel(SuperResolutionModel.SpanClearRealityV1).FileName), "license guard sentinel, not an ONNX graph");
        int deviceEvents = 0;
        image.DeviceChanged += _ => Interlocked.Increment(ref deviceEvents);
        video.DeviceChanged += _ => Interlocked.Increment(ref deviceEvents);
        interpolation.DeviceChanged += _ => Interlocked.Increment(ref deviceEvents);
        foreach (var alphaType in new[] { SKAlphaType.Opaque, SKAlphaType.Premul })
        {
            backend.Current = new(true, false, null);
            await license.RefreshAsync();
            using var source = new SKBitmap(1, 1, SKColorType.Bgra8888, alphaType);
            // 反射只控制真实生产同步边界，不替换待验证的门控或调用链。
            object gate = typeof(SuperResolutionService).GetField("_inferenceGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(image)!;
            using var gateHeld = new ManualResetEventSlim();
            using var releaseGate = new ManualResetEventSlim();
            var holder = new Thread(() => { lock (gate) { gateHeld.Set(); releaseGate.Wait(); } }) { IsBackground = true };
            var result = new TaskCompletionSource<SKBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                try { result.TrySetResult(image.UpscaleAsync(source, SuperResolutionModel.SpanClearRealityV1, 2).GetAwaiter().GetResult()); }
                catch (Exception ex) { result.TrySetException(ex); }
            }) { IsBackground = true };
            holder.Start();
            Check(gateHeld.Wait(TimeSpan.FromSeconds(2)), "未获取生产图片推理锁。");
            worker.Start();
            try
            {
                await EventuallyAsync(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0 && !result.Task.IsCompleted,
                    "图片操作未进入真实推理锁等待。");
                backend.Current = new(false, false, null);
                await license.RefreshAsync();
            }
            finally
            {
                releaseGate.Set();
                Check(holder.Join(TimeSpan.FromSeconds(2)), "图片锁持有线程未释放。");
            }
            await ThrowsAsync<LicenseRequiredException>(() => result.Task);
            Check(worker.Join(TimeSpan.FromSeconds(2)), "图片推理等待线程未退出。");
        }

        await CheckQueuedSessionAsync(video, () => video.CreateSessionAsync(new VideoTranscodeOptions { SuperResolutionEnabled = true }));
        await CheckQueuedSessionAsync(interpolation, () => interpolation.CreateSessionAsync(new VideoTranscodeOptions { InterpolationMode = VideoInterpolationMode.Rife }, 32, 32));
        Check(requests.Calls == beforeRequests && deviceEvents == 0,
            "排队期间许可失效后仍触发下载或设备会话。");

        async Task CheckQueuedSessionAsync(object service, Func<Task> create)
        {
            backend.Current = new(true, false, null);
            await license.RefreshAsync();
            var gate = (SemaphoreSlim)service.GetType().GetField("_sessionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            await gate.WaitAsync();
            Task pending;
            try
            {
                pending = create();
                Check(!pending.IsCompleted, "视频会话没有等待实际独占门。");
                backend.Current = new(false, false, null);
                await license.RefreshAsync();
            }
            finally { gate.Release(); }
            await ThrowsAsync<LicenseRequiredException>(() => pending);
        }
    }
}
