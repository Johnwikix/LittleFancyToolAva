using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lang.Avalonia;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels
{
    public partial class HomeViewModel : ViewModelBase
    {
        private static readonly Dictionary<Type, (string NameKey, string DescKey)> TileKeyMap = new()
        {
            [typeof(SerialPortViewModel)] = ("Home.Tile_Serial_Name", "Home.Tile_Serial_Desc"),
            [typeof(TcpServerViewModel)] = ("Home.Tile_TCP_Name", "Home.Tile_TCP_Desc"),
            [typeof(UdpViewModel)] = ("Home.Tile_UDP_Name", "Home.Tile_UDP_Desc"),
            [typeof(SymmetricEncryptionViewModel)] = ("Home.Tile_Symmetric_Name", "Home.Tile_Symmetric_Desc"),
            [typeof(AsymmetricEncryptionViewModel)] = ("Home.Tile_Asymmetric_Name", "Home.Tile_Asymmetric_Desc"),
            [typeof(HashEncryptionViewModel)] = ("Home.Tile_Hash_Name", "Home.Tile_Hash_Desc"),
            [typeof(Base64ViewModel)] = ("Home.Tile_Base64_Name", "Home.Tile_Base64_Desc"),
            [typeof(FolderCompareViewModel)] = ("Home.Tile_FolderCompare_Name", "Home.Tile_FolderCompare_Desc"),
            [typeof(FileEncryptionViewModel)] = ("Home.Tile_FileEncrypt_Name", "Home.Tile_FileEncrypt_Desc"),
            [typeof(Img2Base64ViewModel)] = ("Home.Tile_Img2Base64_Name", "Home.Tile_Img2Base64_Desc"),
            [typeof(ImageConvertViewModel)] = ("Home.Tile_ImageConvert_Name", "Home.Tile_ImageConvert_Desc"),
        };

        public Action<PageNavigationItem>? NavigateToPage { get; set; }

        public ObservableCollection<PageNavigationItem> ToolGroups { get; }

        public HomeViewModel(IEnumerable<PageNavigationItem> categories)
        {
            ToolGroups = new ObservableCollection<PageNavigationItem>();
            foreach (var category in categories)
            {
                foreach (var child in category.Children)
                {
                    ApplyDescription(child);
                }
                ToolGroups.Add(category);
            }

            I18nManager.Instance.CultureChanged += (_, _) => RefreshAll();
        }

        private void RefreshAll()
        {
            foreach (var category in ToolGroups)
            {
                foreach (var child in category.Children)
                {
                    ApplyDescription(child);
                }
            }
        }

        private static void ApplyDescription(PageNavigationItem child)
        {
            if (child.Content is null) return;
            if (TileKeyMap.TryGetValue(child.Content.GetType(), out var keys))
            {
                child.Description = LocalizationRegistry.Get(keys.DescKey);
            }
        }

        [RelayCommand]
        private void NavigateToTool(PageNavigationItem item)
        {
            NavigateToPage?.Invoke(item);
        }
    }
}
