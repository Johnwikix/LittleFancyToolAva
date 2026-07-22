using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.ViewModels;
using LittleFancyToolAva.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace LittleFancyToolAva
{
    public partial class App : Application
    {
        private AppObserveModel? _appObserveModel;
        private ServiceProvider? _serviceProvider;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
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

                desktop.Exit += (_, _) => host.SaveState();
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    Path.Combine(AppContext.BaseDirectory, "logs", "tool-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    shared: true)
                .CreateLogger();

            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog(dispose: true);
            });

            services.AddSingleton<AppObserveModel>();
            services.AddSingleton<AppPreferences>(sp => sp.GetRequiredService<AppObserveModel>().Preferences);
            services.AddSingleton<FileService>();
            services.AddSingleton<IViewStateService, ViewStateService>();
            services.AddSingleton<ApplicationHostService>();
            services.AddSingleton<NavigationService>();
            services.AddSingleton<NavigationFactory>();

            services.AddKeyedSingleton<IEncryptionSymmetric, AESEncryption>("AES");
            services.AddKeyedSingleton<IEncryptionSymmetric, DESEncryption>("DES");
            services.AddKeyedSingleton<IEncryptionSymmetric, SM4Encryption>("SM4");
            services.AddSingleton<IEncryptionCode, Base64Encryption>();
            services.AddSingleton<IEncryptionAbstract, SHAEncrpytion>();
            services.AddSingleton<INotificationService, ContentDialogNotificationService>();
            services.AddSingleton<IDialogService, ContentDialogService>();
            services.AddSingleton<IFileDialogService, FileDialogService>();
            services.AddSingleton<ISerialPortService, SerialPortService>();
            services.AddSingleton<ITcpServerService, TcpServerService>();
            services.AddSingleton<IUdpService, UdpService>();
            services.AddSingleton<IRabbitMqService, RabbitMqService>();

            services.AddTransient<MainWindowViewModel>();
            services.AddTransient<HomeViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddTransient<AesViewModel>();
            services.AddTransient<DesViewModel>();
            services.AddTransient<Sm4ViewModel>();
            services.AddTransient<RsaViewModel>();
            services.AddTransient<Sm2ViewModel>();
            services.AddTransient<Md5ViewModel>();
            services.AddTransient<ShaViewModel>();
            services.AddTransient<Sm3ViewModel>();
            services.AddTransient<Base64ViewModel>();
            services.AddTransient<SerialPortViewModel>();
            services.AddTransient<TcpServerViewModel>();
            services.AddTransient<UdpViewModel>();
            services.AddTransient<RabbitMqViewModel>();

            services.AddSingleton<IFileEncryptionService, FileEncryptionService>();
            services.AddSingleton<IFolderCompareService, FolderCompareService>();
            services.AddSingleton<IImageConversionService, ImageConversionService>();
            services.AddSingleton<IIconConversionService, IconConversionService>();

            services.AddTransient<FileEncryptionViewModel>();
            services.AddTransient<FolderCompareViewModel>();
            services.AddTransient<Img2Base64ViewModel>();
            services.AddTransient<Img2icoViewModel>();
            services.AddTransient<ImgConvertViewModel>();
        }
    }
}
