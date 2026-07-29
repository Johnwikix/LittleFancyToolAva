using Avalonia.Input;
using FluentAvalonia.UI.Windowing;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.ViewModels;

namespace LittleFancyToolAva.Views
{
    public partial class MainWindow : FAAppWindow
    {
        public MainWindow()
        {
            InitializeComponent();

            if (App.Current is App app && app.TryGetService<NavigationFactory>() is { } navFactory)
            {
                navFactory.Register<HomeViewModel, HomeView>();
                navFactory.Register<AesViewModel, AesView>();
                navFactory.Register<DesViewModel, DesView>();
                navFactory.Register<Sm4ViewModel, Sm4View>();
                navFactory.Register<RsaViewModel, RsaView>();
                navFactory.Register<Sm2ViewModel, Sm2View>();
                navFactory.Register<Md5ViewModel, Md5View>();
                navFactory.Register<ShaViewModel, ShaView>();
                navFactory.Register<Sm3ViewModel, Sm3View>();
                navFactory.Register<Base64ViewModel, Base64View>();
                navFactory.Register<SerialPortViewModel, SerialPortView>();
                navFactory.Register<TcpServerViewModel, TcpServerView>();
                navFactory.Register<UdpViewModel, UdpView>();
                navFactory.Register<SettingsViewModel, SettingsView>();
                navFactory.Register<FileEncryptionViewModel, FileEncryptionView>();
                navFactory.Register<FolderCompareViewModel, FolderCompareView>();
                navFactory.Register<Img2Base64ViewModel, Img2Base64View>();
                navFactory.Register<ImageConvertViewModel, ImageConvertView>();
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

        private void AppTitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }
    }
}
