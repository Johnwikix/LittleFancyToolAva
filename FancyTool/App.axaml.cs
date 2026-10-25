using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lang.Avalonia;
using Lang.Avalonia.Json;
using FancyToolAva.Algorithms;
using FancyToolAva.Algorithms.Encryption;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FancyToolAva.ViewModels;
using FancyToolAva.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace FancyToolAva
{
    public partial class App : Application
    {
        private AppObserveModel? _appObserveModel;
        private ServiceProvider? _serviceProvider;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            var initial = LocalizationRegistry.ResolveInitialCulture(
                TryReadStoredLanguage());

            I18nManager.Instance.Register(
                new JsonLangPlugin(),
                initial,
                out var error);

            if (!string.IsNullOrWhiteSpace(error))
            {
                Log.Warning("I18n init: {Error}", error);
            }

            LocalizationStrings.Initialize(initial);
        }

        public static void ForceCultureRefresh()
        {
            var current = I18nManager.Instance.Culture;
            if (current is null) return;
            var temp = current.Name == "zh-CN" ? new CultureInfo("en-US") : new CultureInfo("zh-CN");
            I18nManager.Instance.Culture = temp;
            I18nManager.Instance.Culture = current;
        }

        public T? TryGetService<T>() where T : class
        {
            return _serviceProvider?.GetService<T>();
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _appObserveModel = _serviceProvider.GetRequiredService<AppObserveModel>();
                var host = _serviceProvider.GetRequiredService<ApplicationHostService>();
                host.LoadState();
                host.LoadViewStates();

                RequestedThemeVariant = _appObserveModel.Preferences.ToAvalonia();

                desktop.MainWindow = new MainWindow
                {
                    DataContext = _serviceProvider.GetRequiredService<MainWindowViewModel>(),
                };

                var license = _serviceProvider.GetRequiredService<ILicenseService>();
                desktop.MainWindow.Opened += async (_, _) =>
                {
                    try
                    {
                        // Avalonia 主窗口建立 HWND 后才初始化 Store，购买弹窗绑定同一个窗口。
                        await license.InitializeAsync(desktop.MainWindow.TryGetPlatformHandle()?.Handle ?? 0);
                    }
                    catch (Exception ex) { Log.Warning(ex, "初始化应用商店许可失败"); }
                };
                desktop.MainWindow.Activated += (_, _) => _ = license.RefreshAsync();

                // 非商店渠道仍预热；MSIX 必须等许可就绪，失效许可不触发后台模型下载。
                _ = _serviceProvider.GetRequiredService<ISuperResolutionService>();
                // Touch the FFmpeg validator so the bundled in-process libraries
                // are probed (and encoders enumerated) before the user opens the page.
                _ = _serviceProvider.GetRequiredService<IFfmpegService>();

                desktop.Exit += async (_, _) =>
                {
                    try
                    {
                        host.SaveState();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to save state on exit");
                    }
                    if (_serviceProvider is IDisposable d)
                    {
                        try { d.Dispose(); } catch (Exception ex) { Log.Warning(ex, "ServiceProvider dispose failed"); }
                    }
                    Log.CloseAndFlush();
                };
            }

            ForceCultureRefresh();
            LocalizationStrings.Reload();

            base.OnFrameworkInitializationCompleted();
        }

        private static string? TryReadStoredLanguage()
        {
            try
            {
                var path = Path.Combine(AppPaths.DataDirectory, "preferences.json");
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Preferences", out var prefs)
                    && prefs.TryGetProperty("Language", out var lang))
                {
                    return lang.GetString();
                }
            }
            catch
            {
            }
            return null;
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog(dispose: false);
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
            });

            services.AddSingleton<AppObserveModel>();
            services.AddSingleton<AppPreferences>(sp => sp.GetRequiredService<AppObserveModel>().Preferences);
            services.AddSingleton<IBackdropService, BackdropService>();
            services.AddSingleton<FileService>();
            services.AddSingleton<IViewStateService, ViewStateService>();
            services.AddSingleton<ApplicationHostService>();
            services.AddSingleton<NavigationService>();
            services.AddSingleton<NavigationFactory>();
            services.AddSingleton<ILicenseService, LicenseService>();
            services.AddSingleton<LicenseViewModel>();

            services.AddKeyedSingleton<IEncryptionSymmetric, AESEncryption>("AES");
            services.AddKeyedSingleton<IEncryptionSymmetric, DESEncryption>("DES");
            services.AddKeyedSingleton<IEncryptionSymmetric, SM4Encryption>("SM4");
            services.AddSingleton<IEncryptionCode, Base64Encryption>();
            services.AddSingleton<INotificationService, ContentDialogNotificationService>();
            services.AddSingleton<IDialogService, ContentDialogService>();
            services.AddSingleton<IFileDialogService, FileDialogService>();
            services.AddSingleton<ISerialPortService, SerialPortService>();
            services.AddSingleton<ITcpServerService, TcpServerService>();
            services.AddSingleton<IUdpService, UdpService>();
            services.AddSingleton<TcpLinkTestService>();
            services.AddSingleton<UdpLinkTestService>();

            services.AddTransient<MainWindowViewModel>();
            services.AddTransient<HomeViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddTransient<SymmetricEncryptionViewModel>();
            services.AddTransient<AsymmetricEncryptionViewModel>();
            services.AddTransient<HashEncryptionViewModel>();
            services.AddTransient<Base64ViewModel>();
            services.AddSingleton<SerialPortViewModel>();
            services.AddSingleton<TcpServerViewModel>();
            services.AddSingleton<UdpViewModel>();

            services.AddSingleton<IFileEncryptionService, FileEncryptionService>();
            services.AddSingleton<IFolderCompareService, FolderCompareService>();
            services.AddHttpClient<ModelDownloadService>();
            services.AddSingleton<IVideoSuperResolutionService>(sp => new VideoSuperResolutionService(
                sp.GetRequiredService<ModelDownloadService>(), sp.GetRequiredService<ILogger<VideoSuperResolutionService>>(),
                sp.GetRequiredService<AppPreferences>(), sp.GetRequiredService<ILicenseService>()));
            services.AddSingleton<IVideoInterpolationService>(sp => new VideoInterpolationService(
                sp.GetRequiredService<ModelDownloadService>(), sp.GetRequiredService<ILogger<VideoInterpolationService>>(),
                sp.GetRequiredService<ILicenseService>()));
            services.AddSingleton<ISuperResolutionService>(sp => new SuperResolutionService(
                sp.GetRequiredService<ILogger<SuperResolutionService>>(),
                sp.GetRequiredService<AppPreferences>(),
                sp.GetRequiredService<ModelDownloadService>(),
                sp.GetRequiredService<ILicenseService>()));
            services.AddSingleton<IImageConversionService, ImageConversionService>();

            services.AddSingleton<IFfmpegService, FfmpegService>();
            services.AddSingleton<IVideoTranscodeService>(sp => new VideoTranscodeService(
                sp.GetRequiredService<ILogger<VideoTranscodeService>>(), sp.GetRequiredService<IVideoSuperResolutionService>(),
                sp.GetRequiredService<IVideoInterpolationService>(), sp.GetRequiredService<ILicenseService>()));
            services.AddSingleton<AudioEnhancement.IAudioUpsamplingBackend, AudioEnhancement.ManagedDspUpsamplingBackend>();
            services.AddSingleton<AudioEnhancement.AudioUpsamplingBackendRegistry>();
            services.AddSingleton<IAudioConvertService, AudioConvertService>();
            services.AddSingleton<Services.Lyrics.PcmAudioDecoder>();
            services.AddSingleton<Services.Lyrics.LyricsModelService>();
            services.AddSingleton<Services.Lyrics.LyricsExtractionService>();
            services.AddSingleton<LyricsExtractionViewModel>();

            services.AddTransient<FileEncryptionViewModel>();
            services.AddTransient<FolderCompareViewModel>();
            services.AddTransient<Img2Base64ViewModel>();
            services.AddTransient<ImageConvertViewModel>();
            services.AddTransient<VideoTranscodeViewModel>();
            services.AddTransient<AudioConvertViewModel>();
        }
    }
}
