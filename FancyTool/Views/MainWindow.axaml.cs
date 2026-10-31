using Avalonia.Input;
using FluentAvalonia.UI.Windowing;
using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using Serilog;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FancyToolAva.ViewModels;

namespace FancyToolAva.Views
{
    public partial class MainWindow : FAAppWindow
    {
        public MainWindow()
        {
            InitializeComponent();

            if (App.Current is App app && app.TryGetService<NavigationFactory>() is { } navFactory)
            {
                app.TryGetService<IBackdropService>()?.Attach(this, RootLayers);

                navFactory.Register<HomeViewModel, HomeView>();
                navFactory.Register<SymmetricEncryptionViewModel, SymmetricEncryptionView>();
                navFactory.Register<AsymmetricEncryptionViewModel, AsymmetricEncryptionView>();
                navFactory.Register<HashEncryptionViewModel, HashEncryptionView>();
                navFactory.Register<Base64ViewModel, Base64View>();
                navFactory.Register<TcpServerViewModel, TcpServerView>();
                navFactory.Register<UdpViewModel, UdpView>();
                navFactory.Register<SerialPortViewModel, SerialPortView>();
                navFactory.Register<SettingsViewModel, SettingsView>();
                navFactory.Register<FileEncryptionViewModel, FileEncryptionView>();
                navFactory.Register<FolderCompareViewModel, FolderCompareView>();
                navFactory.Register<Img2Base64ViewModel, Img2Base64View>();
                navFactory.Register<ImageConvertViewModel, ImageConvertView>();
                navFactory.Register<VideoTranscodeViewModel, VideoTranscodeView>();
                navFactory.Register<AudioConvertViewModel, AudioConvertView>();
                navFactory.Register<LyricsExtractionViewModel, LyricsExtractionView>();
                FrameView.NavigationPageFactory = navFactory;
            }

            NavigationService.Instance.SetFrame(FrameView);

            Loaded += (_, _) =>
            {
                if (DataContext is MainWindowViewModel vm && vm.SelectedPage is PageNavigationItem page && page.Content != null)
                {
                    NavigationService.Instance.NavigateFromContext(page.Content);
                }
            };
        }

        protected override void OnApplyTemplate(Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            TitleBar.ExtendsContentIntoTitleBar = true;
            TitleBar.Height = 48;
        }

        internal async Task ShowLicensePromptAsync(ILicenseService license)
        {
            if (DataContext is not MainWindowViewModel vm || license.CanUseApplication || !IsVisible) return;
            var dialog = new FAContentDialog
            {
                Title = vm.License.TitleText,
                Content = new TextBlock
                {
                    Text = vm.License.StatusText,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 480
                },
                PrimaryButtonText = vm.License.ShowPurchase ? LocalizationRegistry.Get("License.Purchase") : string.Empty,
                CloseButtonText = LocalizationRegistry.Get("Common.Button_Close"),
                DefaultButton = FAContentDialogButton.Primary
            };
            void OnLicenseChanged()
            {
                if (license.CanUseApplication) dialog.Hide();
            }
            license.StateChanged += OnLicenseChanged;
            try
            {
                if (await dialog.ShowAsync(this) == FAContentDialogResult.Primary)
                    await vm.License.PurchaseCommand.ExecuteAsync(null);
            }
            catch (Exception ex) { Log.Warning(ex, "显示购买提示失败，工具保持锁定"); }
            finally { license.StateChanged -= OnLicenseChanged; }
        }

        private void AppTitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }
    }
}
