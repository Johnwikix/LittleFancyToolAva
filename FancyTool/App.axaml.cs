using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FluentAvalonia.UI.Controls;
using Lang.Avalonia;
using Lang.Avalonia.Json;
using FancyToolAva.Algorithms;
using FancyToolAva.Algorithms.Encryption;
using FancyToolAva.Cli;
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

            LocalizationRegistry.Provider = new LangLocalizationProvider();
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

                var license = _serviceProvider.GetRequiredService<ILicenseService>();
                var window = new MainWindow
                {
                    DataContext = _serviceProvider.GetRequiredService<MainWindowViewModel>(),
                };
                desktop.MainWindow = window;
                var mainViewModel = (MainWindowViewModel)window.DataContext;
                mainViewModel.WorkspaceRevoked += () => LicenseSessionRestarter.Restart(() =>
                {
                    window.IsEnabled = false;
                    window.Hide();
                }, host.SaveState);

                window.Opened += async (_, _) =>
                {
                    await license.InitializeAsync(window.TryGetPlatformHandle()?.Handle ?? 0);
                    if (!license.CanUseApplication) await window.ShowLicensePromptAsync(license);
                    else await ShowCliSkillPromptIfNeededAsync(window);
                };
                window.Activated += (_, _) => _ = license.RefreshAsync();

                desktop.Exit += async (_, _) =>
                {
                    try
                    {
                        // 锁定启动没有注册任何页面，不能用空页面集合覆盖已保存的 ViewState。
                        if (mainViewModel.HasStartedTools) host.SaveState();
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

        /// <summary>首次启动询问是否把 CLI 用法写入本机各主流 agent 的技能目录（可选附加 Qoder AGENTS.md）。</summary>
        private async Task ShowCliSkillPromptIfNeededAsync(Window window)
        {
            if (_appObserveModel is null || _serviceProvider is null) return;
            AppPreferences preferences = _appObserveModel.Preferences;
            if (preferences.CliSkillPromptCompleted || !window.IsVisible) return;

            bool zh = (I18nManager.Instance.Culture?.Name ?? CultureInfo.CurrentUICulture.Name)
                .StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            // Message 的 {0} 是"agent 名: 技能路径"逐行列表，{1} 是 Qoder AGENTS.md 路径。
            string agentLines = string.Join("\n", SkillInstaller.Agents.Select(a =>
                $"{a.Title}: {Path.Combine(SkillInstaller.UserHome, a.SkillDirectory, "SKILL.md")}"));
            var dialog = new FAContentDialog
            {
                Title = LocalizationRegistry.Get("SkillPrompt.Title"),
                Content = new SelectableTextBlock
                {
                    Text = LocalizationRegistry.Get("SkillPrompt.Message", agentLines, SkillInstaller.AgentsFilePath),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    MaxWidth = 560,
                    Margin = new Avalonia.Thickness(20),
                },
                PrimaryButtonText = LocalizationRegistry.Get("SkillPrompt.Button_Skill"),
                SecondaryButtonText = LocalizationRegistry.Get("SkillPrompt.Button_Both"),
                CloseButtonText = LocalizationRegistry.Get("SkillPrompt.Button_NotNow"),
                DefaultButton = FAContentDialogButton.Primary,
            };

            FAContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync(window);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "显示 CLI 技能安装提示失败");
                return;
            }

            preferences.CliSkillPromptCompleted = true;
            var notifications = _serviceProvider.GetRequiredService<INotificationService>();
            // 主按钮：全部 agent 的技能；次按钮：技能 + Qoder AGENTS.md 受管块。
            (bool installSkills, bool agentsMd)? choice = result switch
            {
                FAContentDialogResult.Primary => (true, false),
                FAContentDialogResult.Secondary => (true, true),
                _ => null,
            };
            try
            {
                if (choice is null)
                {
                    notifications.ShowInfo(LocalizationRegistry.Get("SkillPrompt.Msg_Skipped"));
                }
                else
                {
                    IReadOnlyList<SkillInstallResult> installed = await SkillInstaller.InstallAsync(
                        agents: null, includeQoderAgentsMd: choice.Value.agentsMd, zh, force: false);
                    List<string> changed = installed.Where(r => r.Changed).Select(r => r.Path).ToList();
                    if (changed.Count > 0)
                        notifications.ShowSuccess(LocalizationRegistry.Get("SkillPrompt.Msg_Installed", string.Join("\n", changed)));
                    else
                        notifications.ShowInfo(LocalizationRegistry.Get("SkillPrompt.Msg_UpToDate"));
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "写入 CLI 技能失败");
                notifications.ShowError(LocalizationRegistry.Get("SkillPrompt.Msg_InstallFailed", ex.Message));
            }
            finally
            {
                try { _serviceProvider.GetRequiredService<ApplicationHostService>().SaveState(); }
                catch (Exception ex) { Log.Warning(ex, "保存 CLI 技能提示状态失败"); }
            }
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
            services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
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
            services.AddSingleton<IVideoSuperResolutionService, VideoSuperResolutionService>();
            services.AddSingleton<IVideoInterpolationService, VideoInterpolationService>();
            services.AddSingleton<ISuperResolutionService>(sp => new SuperResolutionService(
                sp.GetRequiredService<ILogger<SuperResolutionService>>(),
                sp.GetRequiredService<AppPreferences>(),
                sp.GetRequiredService<ModelDownloadService>()));
            services.AddSingleton<IImageConversionService, ImageConversionService>();

            services.AddSingleton<IFfmpegService, FfmpegService>();
            services.AddSingleton<IVideoTranscodeService, VideoTranscodeService>();
            services.AddSingleton<AudioEnhancement.IAudioUpsamplingBackend, AudioEnhancement.ManagedDspUpsamplingBackend>();
            services.AddSingleton<AudioEnhancement.AudioUpsamplingBackendRegistry>();
            services.AddSingleton<IAudioConvertService, AudioConvertService>();

            services.AddTransient<FileEncryptionViewModel>();
            services.AddTransient<FolderCompareViewModel>();
            services.AddTransient<Img2Base64ViewModel>();
            services.AddTransient<ImageConvertViewModel>();
            services.AddTransient<VideoTranscodeViewModel>();
            services.AddTransient<AudioConvertViewModel>();
        }
    }
}
