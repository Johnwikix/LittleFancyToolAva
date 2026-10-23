using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FancyToolAva;
using FancyToolAva.Models;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.Services;
using FancyToolAva.ViewModels;
using FancyToolAva.Views;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunImageModelCases(string workDir, bool inspect)
    {
        try
        {
            string data = Path.GetFullPath(Path.Combine(workDir, "app-data"));
            Environment.SetEnvironmentVariable("FANCYTOOL_DATA_DIRECTORY", data);
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "view-states.json"),
                "{\"ImageConvertView\":{\"SelectedSrModelIndex\":3,\"SelectedSrScaleIndex\":1,\"IsSuperResolutionEnabled\":true}}");
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            AppPaths.InitializeModelsDirectory(null);

            // 保留真实下载/重试/失败收尾逻辑，仅用离线 404 响应替代网络边界。
            using var requests = new ImageModelRequestProbe();
            using var http = new HttpClient(requests);
            using var sr = new SuperResolutionService(NullLogger<SuperResolutionService>.Instance,
                new AppPreferences(), new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance));
            PumpUntil(() => sr.WarmupTask.IsCompleted);
            sr.WarmupTask.GetAwaiter().GetResult();
            string[] files = ["RealESRGAN_x4plus.onnx", "RealESRGAN_x4plus_anime.onnx",
                "realesr-general-x4v3_fp16.onnx", "4x-ClearRealityV1_fp16.onnx"];
            string[] downloads = ["RealESRGAN_x4plus_fp16.onnx", "RealESRGAN_x4plus_anime_6B_fp16.onnx",
                "realesr-general-x4v3_fp16.onnx", "4x-ClearRealityV1-fp16-opset17.onnx"];
            File.WriteAllText(Path.Combine(workDir, "download-requests.json"), JsonSerializer.Serialize(requests.Files.ToArray()));
            Check(requests.Files.Distinct().Order().SequenceEqual(downloads.Order()),
                "自动下载清单包含已移除模型或漏掉保留模型：" + string.Join(", ", requests.Files));
            Check(sr.AvailableModels.SequenceEqual(files.Select(Path.GetFileNameWithoutExtension)), "服务模型列表与下载清单不同步。");
            Check(!sr.IsDownloading && sr.LastDownloadError != null, "离线下载失败未完成收尾。");
            var models = Enum.GetValues<SuperResolutionModel>();
            Check(models.Select(model => (int)model).SequenceEqual(new[] { 0, 1, 2, 4 }), "稳定模型编号发生变化。");
            foreach (var (model, file) in models.Zip(files))
                Check(ModelManifest.GetImageModel(model).FileName == file && ModelManifest.GetImageModel(model).NativeScale == 4,
                    "模型文件或原生倍率映射不正确。");

            // 旧缓存仍存在时，服务也必须拒绝旧编号，不能尝试加载原生会话。
            File.WriteAllText(Path.Combine(AppPaths.ModelsDirectory, "4x-UltraSharp_fp16.onnx"), "legacy cache");
            File.WriteAllText(Path.Combine(AppPaths.ModelsDirectory, "2xLiveActionV1_SPAN.onnx"), "legacy cache");
            using var source = new SKBitmap(1, 1);
            foreach (int removed in new[] { 3, 5, -1, int.MaxValue })
            {
                try
                {
                    sr.UpscaleAsync(source, (SuperResolutionModel)removed, 2).GetAwaiter().GetResult();
                    throw new InvalidOperationException("服务接受了已移除或无效的模型编号。");
                }
                catch (ArgumentOutOfRangeException) { }
            }

            var states = new ViewStateService(NullLogger<ViewStateService>.Instance);
            states.LoadAll();
            var vm = new ImageConvertViewModel(new ImageConversionService(NullLogger<ImageConversionService>.Instance),
                sr, new SettingsTestDialogs(), new ImageModelTestNotifications(), states);
            Check(vm.AvailableSuperResolutionModels[vm.SelectedSrModelIndex] == SuperResolutionModel.SpanClearRealityV1
                && vm.SelectedSrScaleIndex == 1 && vm.IsSuperResolutionEnabled, "构造期间恢复旧选择失败。");
            foreach (int legacy in new[] { 0, 1, 2, 3, 4, 5, -1, int.MaxValue })
            {
                ((IViewState)vm).RestoreState(new ImageConvertViewState
                {
                    SelectedSrModelIndex = legacy, SelectedSrScaleIndex = 1, IsSuperResolutionEnabled = true
                });
                int expected = legacy is >= 0 and <= 2 ? legacy : 4;
                Check((int)vm.AvailableSuperResolutionModels[vm.SelectedSrModelIndex] == expected, "旧模型选择迁移错误。");
                states.SaveAll();
                var saved = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(data, "view-states.json")),
                    ViewStatesJsonContext.Default.ViewStatesRoot)!.ImageConvertView!;
                Check(saved.SelectedSrModelIndex == expected, "保存时误将下拉框索引当作稳定编号。");
                ((IViewState)vm).RestoreState(saved);
                Check((int)vm.AvailableSuperResolutionModels[vm.SelectedSrModelIndex] == expected && vm.SelectedSrScaleIndex == 1,
                    "重复保存/恢复改变了模型或倍率。");
            }

            var view = new ImageConvertView { DataContext = vm };
            var window = new Window { Width = 1100, Height = 800, Content = view, Title = "Image model removal smoke test" };
            window.Show();
            PumpUntil(() => view.Bounds.Width > 1000);
            var modelSelector = view.GetVisualDescendants().OfType<ComboBox>()
                .Single(control => AutomationProperties.GetAutomationId(control) == "ImageSrModel");
            var scaleSelector = view.GetVisualDescendants().OfType<ComboBox>()
                .Single(control => AutomationProperties.GetAutomationId(control) == "ImageSrScale");
            Check(modelSelector.ItemCount == 4 && scaleSelector.ItemCount == 2, "真实页面仍显示旧模型或倍率缺失。");
            Check(vm.AvailableSrModelLabels.All(label => !label.StartsWith("ImageConvert.")
                && !label.Contains("UltraSharp") && !label.Contains("LiveAction")), "模型文案未移除或资源键直出。");
            for (int index = 0; index < models.Length; index++)
            {
                modelSelector.SelectedIndex = index;
                Check(vm.SelectedSrModelIndex == index && scaleSelector.SelectedIndex == 1, "真实双向绑定改变了模型或 4× 倍率。");
            }
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Default })
            {
                Application.Current!.RequestedThemeVariant = theme;
                window.RequestedThemeVariant = theme;
                PumpUntil(() => theme == ThemeVariant.Default || window.ActualThemeVariant == theme);
                view.Background = window.Background;
                window.UpdateLayout();
                using var image = new RenderTargetBitmap(new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height), new Vector(96, 96));
                image.Render(view);
                image.Save(Path.Combine(workDir, $"image-models-{theme.Key}.png"), PngBitmapEncoderOptions.Default);
            }
            if (inspect)
            {
                using var done = new CancellationTokenSource();
                window.Closed += (_, _) => done.Cancel();
                Console.WriteLine($"UI ready: PID={Environment.ProcessId}");
                Dispatcher.UIThread.MainLoop(done.Token);
            }
            else window.Close();
            states.Unregister(vm);
            Console.WriteLine("Image models OK: four download entries, removed IDs rejected with legacy cache, eight legacy states migrated, source-generated persistence and real UI bindings/themes verified.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Image models FAIL: " + ex);
            return 1;
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private sealed class ImageModelRequestProbe : HttpMessageHandler
    {
        public ConcurrentQueue<string> Files { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Files.Enqueue(Path.GetFileName(request.RequestUri!.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ImageModelTestNotifications : INotificationService
    {
        public void ShowError(string message) => throw new InvalidOperationException(message);
        public void ShowSuccess(string message) { }
        public void ShowInfo(string message) { }
        public void ShowWarn(string message) { }
    }
}
