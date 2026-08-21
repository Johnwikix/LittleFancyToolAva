using System.Collections.ObjectModel;
using Avalonia.Threading;
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

            var commCategory = BuildCommCategory(serviceProvider);
            var encryptionCategory = BuildEncryptionCategory(serviceProvider);
            var fileCategory = BuildFileCategory(serviceProvider);
            var imageCategory = BuildImageCategory(serviceProvider);

            var categories = new PageNavigationItem[]
            {
                commCategory, encryptionCategory, fileCategory, imageCategory,
            };

            var homeVm = new HomeViewModel(categories);
            homeVm.NavigateToPage = item =>
            {
                var parent = Pages.FirstOrDefault(p => p.Children.Contains(item));
                if (parent != null)
                    parent.IsExpanded = true;

                // FANavigationView 需要父分类展开、子项容器物化完成后才能正确选中，
                // 否则选中会被回滚（IsSelectionSuppressed）或指示条丢失（AnimateSelectionChanged）。
                // Post 晚于布局循环，届时子容器已就绪。
                Dispatcher.UIThread.Post(() => SelectedPage = item);
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
            categories[0].Label = L.Localize("Nav.Category_Comm");
            categories[0].Children[0].Label = L.Localize("Nav.Item_TCP");
            categories[0].Children[1].Label = L.Localize("Nav.Item_UDP");
            categories[0].Children[2].Label = L.Localize("Nav.Item_Serial");
            categories[1].Label = L.Localize("Nav.Category_Encrypt");
            categories[1].Children[0].Label = L.Localize("Nav.Item_Symmetric");
            categories[1].Children[1].Label = L.Localize("Nav.Item_Asymmetric");
            categories[1].Children[2].Label = L.Localize("Nav.Item_Hash");
            categories[1].Children[3].Label = L.Localize("Nav.Item_Base64");
            categories[2].Label = L.Localize("Nav.Category_File");
            categories[2].Children[0].Label = L.Localize("Nav.Item_FolderCompare");
            categories[2].Children[1].Label = L.Localize("Nav.Item_FileEncrypt");
            categories[3].Label = L.Localize("Nav.Category_Image");
            categories[3].Children[0].Label = L.Localize("Nav.Item_Img2Base64");
            categories[3].Children[1].Label = L.Localize("Nav.Item_ImageConvert");
        }

        private static PageNavigationItem BuildCommCategory(IServiceProvider sp)
        {
            var category = new PageNavigationItem(L.Localize("Nav.Category_Comm"), FASymbol.Globe);
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_TCP"), FASymbol.Link, sp.GetRequiredService<TcpServerViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_UDP"), FASymbol.Wifi4, sp.GetRequiredService<UdpViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_Serial"), FASymbol.Sync, sp.GetRequiredService<SerialPortViewModel>()));
            return category;
        }

        private static PageNavigationItem BuildEncryptionCategory(IServiceProvider sp)
        {
            var category = new PageNavigationItem(L.Localize("Nav.Category_Encrypt"), FASymbol.Permissions);
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_Symmetric"), FASymbol.Permissions, sp.GetRequiredService<SymmetricEncryptionViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_Asymmetric"), FASymbol.Permissions, sp.GetRequiredService<AsymmetricEncryptionViewModel>()));
            category.Children.Add(new PageNavigationItem(L.Localize("Nav.Item_Hash"), FASymbol.Permissions, sp.GetRequiredService<HashEncryptionViewModel>()));
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
