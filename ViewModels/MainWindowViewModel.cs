using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;

namespace LittleFancyToolAva.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        public AppObserveModel AppObserveModel { get; }

        public ObservableCollection<PageNavigationItem> Pages { get; }

        public ObservableCollection<PageNavigationItem> FooterPages { get; }

        public object? SelectedPage
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnSelectedPageChanged(value);
                }
            }
        }

        public MainWindowViewModel(AppObserveModel appObserveModel, IServiceProvider serviceProvider)
        {
            AppObserveModel = appObserveModel;

            FooterPages = new ObservableCollection<PageNavigationItem>
            {
                new PageNavigationItem("设置", FASymbol.Setting, serviceProvider.GetRequiredService<SettingsViewModel>())
            };

            var serialCategory = BuildSerialCategory(serviceProvider);
            var networkCategory = BuildNetworkCategory(serviceProvider);
            var encryptionCategory = BuildEncryptionCategory(serviceProvider);
            var fileCategory = BuildFileCategory(serviceProvider);
            var imageCategory = BuildImageCategory(serviceProvider);

            var categories = new PageNavigationItem[]
            {
                serialCategory, networkCategory, encryptionCategory, fileCategory, imageCategory,
            };

            var homeVm = new HomeViewModel(categories);
            homeVm.NavigateToPage = item =>
            {
                var parent = Pages.FirstOrDefault(p => p.Children.Contains(item));
                if (parent != null)
                    parent.IsExpanded = true;
                SelectedPage = item;
            };

            Pages = new ObservableCollection<PageNavigationItem>
            {
                new PageNavigationItem("首页", FASymbol.Home, homeVm),
            };
            foreach (var cat in categories)
                Pages.Add(cat);

            SelectedPage = Pages[0];
        }

        private static PageNavigationItem BuildSerialCategory(IServiceProvider sp)
        {
            var category = new PageNavigationItem("串口", FASymbol.Remote);
            category.Children.Add(new PageNavigationItem("串口调试", FASymbol.Remote, sp.GetRequiredService<SerialPortViewModel>()));
            return category;
        }

        private static PageNavigationItem BuildNetworkCategory(IServiceProvider sp)
        {
            var category = new PageNavigationItem("网络", FASymbol.Globe);
            category.Children.Add(new PageNavigationItem("TCP", FASymbol.Message, sp.GetRequiredService<TcpServerViewModel>()));
            category.Children.Add(new PageNavigationItem("UDP", FASymbol.Message, sp.GetRequiredService<UdpViewModel>()));
            return category;
        }

        private static PageNavigationItem BuildEncryptionCategory(IServiceProvider sp)
        {
            var category = new PageNavigationItem("加密", FASymbol.Permissions);
            category.Children.Add(new PageNavigationItem("DES", FASymbol.Permissions, sp.GetRequiredService<DesViewModel>()));
            category.Children.Add(new PageNavigationItem("AES", FASymbol.Permissions, sp.GetRequiredService<AesViewModel>()));
            category.Children.Add(new PageNavigationItem("SM4", FASymbol.Permissions, sp.GetRequiredService<Sm4ViewModel>()));
            category.Children.Add(new PageNavigationItem("RSA", FASymbol.Permissions, sp.GetRequiredService<RsaViewModel>()));
            category.Children.Add(new PageNavigationItem("SM2", FASymbol.Permissions, sp.GetRequiredService<Sm2ViewModel>()));
            category.Children.Add(new PageNavigationItem("MD5", FASymbol.Permissions, sp.GetRequiredService<Md5ViewModel>()));
            category.Children.Add(new PageNavigationItem("SHA", FASymbol.Permissions, sp.GetRequiredService<ShaViewModel>()));
            category.Children.Add(new PageNavigationItem("SM3", FASymbol.Permissions, sp.GetRequiredService<Sm3ViewModel>()));
            category.Children.Add(new PageNavigationItem("Base64", FASymbol.Permissions, sp.GetRequiredService<Base64ViewModel>()));
            return category;
        }

        private static PageNavigationItem BuildFileCategory(IServiceProvider sp)
        {
            var category = new PageNavigationItem("文件处理", FASymbol.Document);
            category.Children.Add(new PageNavigationItem("文件夹比较", FASymbol.MoveToFolder, sp.GetRequiredService<FolderCompareViewModel>()));
            category.Children.Add(new PageNavigationItem("文件加解密", FASymbol.ProtectedDocument, sp.GetRequiredService<FileEncryptionViewModel>()));
            return category;
        }

        private static PageNavigationItem BuildImageCategory(IServiceProvider sp)
        {
            var category = new PageNavigationItem("图片处理", FASymbol.Image);
            category.Children.Add(new PageNavigationItem("图片转 Base64", FASymbol.ImageAltText, sp.GetRequiredService<Img2Base64ViewModel>()));
            category.Children.Add(new PageNavigationItem("图片转 ICO", FASymbol.ImageCopy, sp.GetRequiredService<Img2icoViewModel>()));
            category.Children.Add(new PageNavigationItem("图片格式转换", FASymbol.ImageCopyFilled, sp.GetRequiredService<ImgConvertViewModel>()));
            return category;
        }

        private void OnSelectedPageChanged(object? value)
        {
            if (value is PageNavigationItem item && item.Content != null)
            {
                NavigationService.Instance.NavigateFromContext(item.Content);
            }
        }
    }
}