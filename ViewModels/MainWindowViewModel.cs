using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using Lang.Avalonia;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Services;
using Microsoft.Extensions.DependencyInjection;

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

            Pages = new ObservableCollection<PageNavigationItem>();
            FooterPages = new ObservableCollection<PageNavigationItem>
            {
                new PageNavigationItem(L.Localize("Nav.Settings"), FASymbol.Setting, serviceProvider.GetRequiredService<SettingsViewModel>())
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

            var homeItem = new PageNavigationItem(L.Localize("Nav.Home"), FASymbol.Home, homeVm);
            Pages.Add(homeItem);
            foreach (var cat in categories)
                Pages.Add(cat);

            SelectedPage = Pages[0];

            I18nManager.Instance.CultureChanged += (_, _) => RebuildLabels(serviceProvider, homeItem, categories);
        }

        private void RebuildLabels(IServiceProvider sp, PageNavigationItem homeItem, PageNavigationItem[] categories)
        {
            FooterPages[0].Label = L.Localize("Nav.Settings");
            homeItem.Label = L.Localize("Nav.Home");
            categories[0].Label = L.Localize("Nav.Category_Serial");
            categories[0].Children[0].Label = L.Localize("Nav.Item_Serial");
            categories[1].Label = L.Localize("Nav.Category_Network");
            categories[1].Children[0].Label = L.Localize("Nav.Item_TCP");
            categories[1].Children[1].Label = L.Localize("Nav.Item_UDP");
            categories[2].Label = L.Localize("Nav.Category_Encrypt");
            categories[2].Children[0].Label = L.Localize("Nav.Item_DES");
            categories[2].Children[1].Label = L.Localize("Nav.Item_AES");
            categories[2].Children[2].Label = L.Localize("Nav.Item_SM4");
            categories[2].Children[3].Label = L.Localize("Nav.Item_RSA");
            categories[2].Children[4].Label = L.Localize("Nav.Item_SM2");
            categories[2].Children[5].Label = L.Localize("Nav.Item_MD5");
            categories[2].Children[6].Label = L.Localize("Nav.Item_SHA");
            categories[2].Children[7].Label = L.Localize("Nav.Item_SM3");
            categories[2].Children[8].Label = L.Localize("Nav.Item_Base64");
            categories[3].Label = L.Localize("Nav.Category_File");
            categories[3].Children[0].Label = L.Localize("Nav.Item_FolderCompare");
            categories[3].Children[1].Label = L.Localize("Nav.Item_FileEncrypt");
            categories[4].Label = L.Localize("Nav.Category_Image");
            categories[4].Children[0].Label = L.Localize("Nav.Item_Img2Base64");
            categories[4].Children[1].Label = L.Localize("Nav.Item_ImageConvert");
        }

        private static PageNavigationItem BuildSerialCategory(IServiceProvider sp)
        {
            var category = new PageNavigationItem(L.Localize("Nav.Category_Serial"), FASymbol.Remote);
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_Serial"), FASymbol.Remote, sp.GetRequiredService<SerialPortViewModel>()));
            return category;
        }

        private static PageNavigationItem BuildNetworkCategory(IServiceProvider sp)
        {
            var category = new PageNavigationItem(L.Localize("Nav.Category_Network"), FASymbol.Globe);
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_TCP"), FASymbol.Message, sp.GetRequiredService<TcpServerViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_UDP"), FASymbol.Message, sp.GetRequiredService<UdpViewModel>()));
            return category;
        }

        private static PageNavigationItem BuildEncryptionCategory(IServiceProvider sp)
        {
            var category = new PageNavigationItem(L.Localize("Nav.Category_Encrypt"), FASymbol.Permissions);
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_DES"), FASymbol.Permissions, sp.GetRequiredService<DesViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_AES"), FASymbol.Permissions, sp.GetRequiredService<AesViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_SM4"), FASymbol.Permissions, sp.GetRequiredService<Sm4ViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_RSA"), FASymbol.Permissions, sp.GetRequiredService<RsaViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_SM2"), FASymbol.Permissions, sp.GetRequiredService<Sm2ViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_MD5"), FASymbol.Permissions, sp.GetRequiredService<Md5ViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_SHA"), FASymbol.Permissions, sp.GetRequiredService<ShaViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_SM3"), FASymbol.Permissions, sp.GetRequiredService<Sm3ViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_Base64"), FASymbol.Permissions, sp.GetRequiredService<Base64ViewModel>()));
            return category;
        }

        private static PageNavigationItem BuildFileCategory(IServiceProvider sp)
        {
            var category = new PageNavigationItem(L.Localize("Nav.Category_File"), FASymbol.Document);
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_FolderCompare"), FASymbol.MoveToFolder, sp.GetRequiredService<FolderCompareViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_FileEncrypt"), FASymbol.ProtectedDocument, sp.GetRequiredService<FileEncryptionViewModel>()));
            return category;
        }

        private static PageNavigationItem BuildImageCategory(IServiceProvider sp)
        {
            var category = new PageNavigationItem(L.Localize("Nav.Category_Image"), FASymbol.Image);
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_Img2Base64"), FASymbol.ImageAltText, sp.GetRequiredService<Img2Base64ViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_ImageConvert"), FASymbol.ImageCopyFilled, sp.GetRequiredService<ImageConvertViewModel>()));
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

    internal static class L
    {
        public static string Localize(string key) => LocalizationRegistry.Get(key);
    }
}
