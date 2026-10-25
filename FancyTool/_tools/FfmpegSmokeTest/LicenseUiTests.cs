using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FancyToolAva;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FancyToolAva.ViewModels;
using FancyToolAva.Views;
using FancyToolAva.Views.Controls;
using Lang.Avalonia;
using Microsoft.Extensions.Logging.Abstractions;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunLicenseUiCases(string workDir, bool inspect, bool navigationOnly = false)
    {
        try
        {
            string data = Path.GetFullPath(Path.Combine(workDir, "ui-data-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "preferences.json"), "{\"Preferences\":{\"Language\":\"zh-CN\"}}");
            Environment.SetEnvironmentVariable("FANCYTOOL_DATA_DIRECTORY", data);
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            var access = new LicenseUiAccess();
            using var licenseVm = new LicenseViewModel(access);
            using var requests = new ImageModelRequestProbe();
            using var http = new HttpClient(requests);
            var downloads = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
            var preferences = new AppPreferences();
            using var imageSr = new SuperResolutionService(NullLogger<SuperResolutionService>.Instance, preferences, downloads, access);
            using var videoSr = new VideoSuperResolutionService(downloads, NullLogger<VideoSuperResolutionService>.Instance, preferences, access);
            using var interpolation = new VideoInterpolationService(downloads, NullLogger<VideoInterpolationService>.Instance, access);
            var transcode = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance, videoSr, interpolation, access);
            var ffmpeg = new FfmpegService(NullLogger<FfmpegService>.Instance);
            var validation = ffmpeg.ValidateAsync();
            PumpUntil(() => validation.IsCompleted);
            if (!validation.GetAwaiter().GetResult()) throw new InvalidOperationException("烟测缺少进程内 FFmpeg 库。");
            var dialogs = new SettingsTestDialogs();
            var notifications = new LicenseUiNotifications();
            var states = new SettingsTestViewStates();
            using var imageVm = new ImageConvertViewModel(new ImageConversionService(NullLogger<ImageConversionService>.Instance), imageSr,
                dialogs, notifications, states, access, licenseVm);
            using var videoVm = new VideoTranscodeViewModel(transcode, ffmpeg, dialogs, notifications, states, preferences,
                NullLogger<VideoTranscodeViewModel>.Instance, videoSr, interpolation, access, licenseVm);
            using var settingsVm = new SettingsViewModel(new AppObserveModel(), dialogs,
                new FileService(NullLogger<FileService>.Instance), access, licenseVm);
            imageVm.FileItems.Add(new ImageFileItem(Path.Combine(data, "example.png")));
            videoVm.FileItems.Add(new VideoFileItem(Path.Combine(data, "example.mp4")));
            if (navigationOnly) return RunLicenseNavigationCases(workDir, imageVm, videoVm, access);
            imageVm.IsSuperResolutionEnabled = true;
            videoVm.IsVideoSrEnabled = true;
            videoVm.InterpolationModeIndex = 1;
            var imageView = new ImageConvertView { DataContext = imageVm };
            var videoView = new VideoTranscodeView { DataContext = videoVm };
            var settingsView = new SettingsView { DataContext = settingsVm };
            var content = new ContentControl { Content = imageView };
            var actions = new WrapPanel { Margin = new Thickness(12) };
            void ActionButton(string label, string id, Action action)
            {
                var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 4) };
                AutomationProperties.SetAutomationId(button, id);
                button.Click += (_, _) => action();
                actions.Children.Add(button);
            }
            ActionButton("图片", "LicenseTestImage", () => content.Content = imageView);
            ActionButton("视频", "LicenseTestVideo", () => content.Content = videoView);
            ActionButton("设置", "LicenseTestSettings", () => content.Content = settingsView);
            ActionButton("受限", "LicenseTestInactive", () => access.Set(AppLicenseState.LicenseInactive));
            ActionButton("试用", "LicenseTestTrial", () => access.Set(AppLicenseState.TrialActive));
            ActionButton("已购", "LicenseTestFull", () => access.Set(AppLicenseState.FullLicense));
            ActionButton("普通构建", "LicenseTestBypass", () => access.Set(AppLicenseState.StoreUnavailable));
            ActionButton("English", "LicenseTestEn", () => I18nManager.Instance.Culture = new CultureInfo("en-US"));
            ActionButton("中文", "LicenseTestZh", () => I18nManager.Instance.Culture = new CultureInfo("zh-CN"));
            var layout = new DockPanel();
            DockPanel.SetDock(actions, Dock.Top);
            layout.Children.Add(actions);
            layout.Children.Add(content);
            var window = new Window { Width = 1120, Height = 900, Content = layout, Title = "MSIX AI license UI smoke (fake Store)" };
            layout.Bind(Panel.BackgroundProperty, window.GetResourceObservable("SolidBackgroundFillColorBaseBrush"));
            window.Show();
            PumpUntil(() => imageView.Bounds.Width > 1000);
            var checks = new List<string>();
            void Check(bool condition, string name)
            {
                if (!condition) throw new InvalidOperationException(name);
                checks.Add(name);
            }
            Check(licenseVm.IsNoticeVisible && !imageVm.StartConvertCommand.CanExecute(null)
                && !videoVm.StartTranscodeCommand.CanExecute(null), "受限时保留 AI 选项但拒绝开始");
            Check(requests.Files.IsEmpty, "受限图片服务不在后台下载模型");
            var denied = imageVm.StartConvertCommand.ExecuteAsync(null);
            PumpUntil(() => denied.IsCompleted);
            denied.GetAwaiter().GetResult();
            Check(!imageVm.IsBusy && notifications.Messages.Count > 0, "直接调用图片命令先提示并保持非忙状态");
            imageVm.IsSuperResolutionEnabled = false;
            videoVm.IsVideoSrEnabled = false;
            videoVm.InterpolationModeIndex = 2;
            Check(imageVm.StartConvertCommand.CanExecute(null) && videoVm.StartTranscodeCommand.CanExecute(null), "普通转换与无模型运动补偿不受门控");
            imageVm.IsSuperResolutionEnabled = true;
            videoVm.IsVideoSrEnabled = true;
            videoVm.InterpolationModeIndex = 1;

            foreach (var (page, name) in new (Control, string)[] { (imageView, "image"), (videoView, "video"), (settingsView, "settings") })
            foreach (var (theme, width) in new[] { (ThemeVariant.Light, 1120d), (ThemeVariant.Dark, 760d) })
            {
                content.Content = page;
                window.Width = width;
                Application.Current!.RequestedThemeVariant = theme;
                window.RequestedThemeVariant = theme;
                var settled = Stopwatch.StartNew();
                PumpUntil(() => page.Bounds.Width > width - 80 && window.ActualThemeVariant == theme && settled.ElapsedMilliseconds >= 300);
                window.UpdateLayout();
                var notice = page.GetVisualDescendants().OfType<LicenseNotice>().Single();
                notice.BringIntoView();
                var scrolled = Stopwatch.StartNew();
                PumpUntil(() => scrolled.ElapsedMilliseconds >= 100);
                Check(notice.IsVisible && notice.Bounds.Width > 0, $"{name}/{theme.Key} 显示真实授权入口");
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)layout.Bounds.Width, (int)layout.Bounds.Height), new Vector(96, 96));
                bitmap.Render(layout);
                bitmap.Save(Path.Combine(workDir, $"license-{name}-{theme.Key}.png"), PngBitmapEncoderOptions.Default);
            }
            access.Set(AppLicenseState.TrialActive);
            PumpUntil(() => licenseVm.StatusText.Contains('3') && imageVm.CanToggleImageSr);
            Check(imageVm.IsSuperResolutionEnabled && videoVm.IsVideoSrEnabled && videoVm.InterpolationModeIndex == 1,
                "恢复授权未改写已保存的 AI 选择");
            access.Set(AppLicenseState.LicenseInactive);
            PumpUntil(() => licenseVm.ShowPurchase);
            var purchase = licenseVm.PurchaseCommand.ExecuteAsync(null);
            PumpUntil(() => purchase.IsCompleted && !licenseVm.IsRestricted);
            purchase.GetAwaiter().GetResult();
            Check(ReferenceEquals(imageVm.License, videoVm.License) && ReferenceEquals(settingsVm.License, licenseVm)
                && access.Purchases == 1, "三个页面共享同一购买命令并立即刷新");
            access.Set(AppLicenseState.StoreUnavailable);
            PumpUntil(() => !licenseVm.IsNoticeVisible);
            Check(imageVm.CanToggleImageSr && videoVm.CanToggleVideoSr, "普通构建不显示门控且功能可用");
            access.Set(AppLicenseState.LicenseInactive);
            I18nManager.Instance.Culture = new CultureInfo("en-US");
            PumpUntil(() => licenseVm.TitleText == LocalizationRegistry.Get("License.Title") && !licenseVm.TitleText.Contains("许可"));
            Check(!licenseVm.StatusText.StartsWith("License.") && !licenseVm.TitleText.StartsWith("License."), "英语切换即时刷新且没有资源键直出");
            content.Content = settingsView;
            window.Width = 1120;
            window.RequestedThemeVariant = ThemeVariant.Light;
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            PumpUntil(() => settingsView.Bounds.Width > 1000 && window.ActualThemeVariant == ThemeVariant.Light);
            using (var bitmap = new RenderTargetBitmap(new PixelSize((int)layout.Bounds.Width, (int)layout.Bounds.Height), new Vector(96, 96)))
            {
                bitmap.Render(layout);
                bitmap.Save(Path.Combine(workDir, "license-settings-en-US.png"), PngBitmapEncoderOptions.Default);
            }
            File.WriteAllText(Path.Combine(workDir, "ui-checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"License UI OK: {checks.Count} checks, PID={Environment.ProcessId}");
            if (inspect)
            {
                I18nManager.Instance.Culture = new CultureInfo("zh-CN");
                content.Content = imageView;
                using var closed = new CancellationTokenSource();
                window.Closed += (_, _) => closed.Cancel();
                Dispatcher.UIThread.MainLoop(closed.Token);
            }
            else window.Close();
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("License UI FAIL: " + ex); return 1; }
    }

    private static int RunLicenseNavigationCases(string workDir, ImageConvertViewModel imageVm,
        VideoTranscodeViewModel videoVm, LicenseUiAccess access)
    {
        // MainWindowViewModel 将 VM 放在 PageNavigationItem.Content 中缓存，往返时复用实例。
        var navigation = new NavigationFactory();
        navigation.Register<ImageConvertViewModel, ImageConvertView>();
        navigation.Register<VideoTranscodeViewModel, VideoTranscodeView>();
        imageVm.IsSuperResolutionEnabled = false;
        videoVm.IsVideoSrEnabled = false;
        videoVm.InterpolationModeIndex = 0;
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        foreach (var model in new object[] { imageVm, videoVm, imageVm, videoVm, imageVm })
            Check(ReferenceEquals(navigation.GetPageFromObject(model).DataContext, model),
                $"导航复用缓存实例 {model.GetType().Name}/{checks.Count + 1}");
        Check(imageVm.StartConvertCommand.CanExecute(null) && videoVm.StartTranscodeCommand.CanExecute(null),
            "往返导航后普通转换仍可启动");
        imageVm.IsSuperResolutionEnabled = true;
        videoVm.IsVideoSrEnabled = true;
        videoVm.InterpolationModeIndex = 1;
        Check(!imageVm.StartConvertCommand.CanExecute(null) && !videoVm.StartTranscodeCommand.CanExecute(null),
            "往返导航后受限 AI 仍被拒绝");
        int imageNotifications = 0, videoNotifications = 0;
        imageVm.StartConvertCommand.CanExecuteChanged += (_, _) => imageNotifications++;
        videoVm.StartTranscodeCommand.CanExecuteChanged += (_, _) => videoNotifications++;
        access.Set(AppLicenseState.TrialActive);
        PumpUntil(() => imageVm.StartConvertCommand.CanExecute(null) && videoVm.StartTranscodeCommand.CanExecute(null)
            && imageNotifications > 0 && videoNotifications > 0);
        Check(imageNotifications > 0 && videoNotifications > 0, "往返导航后恢复许可仍通知两个页面的命令");
        access.Set(AppLicenseState.LicenseInactive);
        PumpUntil(() => !imageVm.StartConvertCommand.CanExecute(null) && !videoVm.StartTranscodeCommand.CanExecute(null));
        Check(!imageVm.StartConvertCommand.CanExecute(null) && !videoVm.StartTranscodeCommand.CanExecute(null),
            "往返导航后失效许可再次限制 AI");
        File.WriteAllText(Path.Combine(workDir, "navigation-checks.json"),
            JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"License navigation OK: {checks.Count} checks");
        return 0;
    }

    private sealed class LicenseUiNotifications : INotificationService
    {
        internal List<string> Messages { get; } = [];
        public void ShowError(string message) => Messages.Add(message);
        public void ShowWarn(string message) => Messages.Add(message);
        public void ShowInfo(string message) => Messages.Add(message);
        public void ShowSuccess(string message) => Messages.Add(message);
    }

    // 仅烟测进程注入，正式 MSIX 没有环境变量或文件可伪造许可。
    private sealed class LicenseUiAccess : ILicenseService
    {
        public AppLicenseState State { get; private set; } = AppLicenseState.LicenseInactive;
        public bool IsInitialized => true;
        public bool IsRestricted => State == AppLicenseState.LicenseInactive;
        public bool CanPurchase => State is AppLicenseState.TrialActive or AppLicenseState.LicenseInactive;
        public int? TrialRemainingDays => State == AppLicenseState.TrialActive ? 3 : null;
        public DateTimeOffset? TrialExpiration => State == AppLicenseState.TrialActive ? DateTimeOffset.UtcNow.AddDays(3) : null;
        public event Action? StateChanged;
        public int Purchases { get; private set; }
        public bool CanUseFeature(LicenseFeature feature) => !IsFeatureRestricted(feature);
        public bool IsFeatureRestricted(LicenseFeature feature) => IsRestricted && feature != LicenseFeature.None;
        public void ThrowIfRestricted(LicenseFeature feature) { if (IsFeatureRestricted(feature)) throw new LicenseRequiredException(); }
        public Task EnsureFeatureAsync(LicenseFeature feature, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); ThrowIfRestricted(feature); return Task.CompletedTask;
        }
        public Task InitializeAsync(nint ownerHandle) => Task.CompletedTask;
        public Task RefreshAsync() => Task.CompletedTask;
        public Task PurchaseAsync() { Purchases++; Set(AppLicenseState.FullLicense); return Task.CompletedTask; }
        public void Set(AppLicenseState state) { State = state; StateChanged?.Invoke(); }
        public void Dispose() { }
    }
}
