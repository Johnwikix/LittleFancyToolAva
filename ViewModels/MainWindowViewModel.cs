using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.Views;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;

namespace LittleFancyToolAva.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        public AppObserveModel AppObserveModel { get; }

        public ObservableCollection<PageNavigationItem> Pages { get; }

        [ObservableProperty]
        private PageNavigationItem? _selectedPage;

        public MainWindowViewModel(AppObserveModel appObserveModel, IServiceProvider serviceProvider)
        {
            AppObserveModel = appObserveModel;

            Pages = new ObservableCollection<PageNavigationItem>
            {
                new PageNavigationItem("首页", FASymbol.Home, new HomeViewModel(appObserveModel)),
                new PageNavigationItem("串口调试", FASymbol.Document, serviceProvider.GetRequiredService<SerialPortViewModel>()),
                new PageNavigationItem("Modbus Poll", FASymbol.Document, serviceProvider.GetRequiredService<ModbusPollViewModel>()),
                new PageNavigationItem("Modbus Slave", FASymbol.Document, serviceProvider.GetRequiredService<ModbusSlaveViewModel>()),
                new PageNavigationItem("Sockets", FASymbol.Message, serviceProvider.GetRequiredService<TcpServerViewModel>()),
                new PageNavigationItem("RSA", FASymbol.Page2, serviceProvider.GetRequiredService<RsaViewModel>()),
                new PageNavigationItem("SM2", FASymbol.Page2, serviceProvider.GetRequiredService<Sm2ViewModel>()),
                new PageNavigationItem("AES", FASymbol.Document, serviceProvider.GetRequiredService<AesViewModel>()),
                new PageNavigationItem("DES", FASymbol.Document, serviceProvider.GetRequiredService<DesViewModel>()),
                new PageNavigationItem("SM4", FASymbol.Document, serviceProvider.GetRequiredService<Sm4ViewModel>()),
                new PageNavigationItem("MD5", FASymbol.Page2, serviceProvider.GetRequiredService<Md5ViewModel>()),
                new PageNavigationItem("SHA", FASymbol.Page2, serviceProvider.GetRequiredService<ShaViewModel>()),
                new PageNavigationItem("SM3", FASymbol.Page2, serviceProvider.GetRequiredService<Sm3ViewModel>()),
                new PageNavigationItem("Base64", FASymbol.Page2, serviceProvider.GetRequiredService<Base64ViewModel>()),
                new PageNavigationItem("文件加解密", FASymbol.Document, serviceProvider.GetRequiredService<FileEncryptionViewModel>()),
                new PageNavigationItem("文件夹比较", FASymbol.Document, serviceProvider.GetRequiredService<FolderCompareViewModel>()),
                new PageNavigationItem("图片转Base64", FASymbol.Page2, serviceProvider.GetRequiredService<Img2Base64ViewModel>()),
                new PageNavigationItem("图片转ICO", FASymbol.Page2, serviceProvider.GetRequiredService<Img2icoViewModel>()),
                new PageNavigationItem("图片格式转换", FASymbol.Page2, serviceProvider.GetRequiredService<ImgConvertViewModel>()),
                new PageNavigationItem("设置", FASymbol.Setting, new SettingsViewModel(appObserveModel)),
            };

            SelectedPage = Pages[0];
        }

        partial void OnSelectedPageChanged(PageNavigationItem? value)
        {
            if (value?.Content != null)
            {
                NavigationService.Instance.NavigateFromContext(value.Content);
            }
        }
    }
}
