using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Services;
using System.Collections.ObjectModel;

namespace LittleFancyToolAva.ViewModels
{
    public partial class HomeViewModel : ViewModelBase
    {
        private static readonly Dictionary<string, string> ToolDescriptions = new()
        {
            ["串口调试"] = "串口数据收发与调试",
            ["TCP"] = "TCP 服务器调试工具",
            ["UDP"] = "UDP 通信调试",
            ["DES"] = "DES 加解密",
            ["AES"] = "AES 加解密",
            ["SM4"] = "SM4 国密加解密",
            ["RSA"] = "RSA 非对称加解密",
            ["SM2"] = "SM2 国密非对称加解密",
            ["MD5"] = "MD5 哈希计算",
            ["SHA"] = "SHA 系列哈希计算",
            ["SM3"] = "SM3 国密哈希计算",
            ["Base64"] = "Base64 编码/解码",
            ["文件夹比较"] = "对比两个文件夹内容差异",
            ["文件加解密"] = "文件级加解密操作",
            ["图片转 Base64"] = "将图片转换为 Base64 编码",
            ["图片转 ICO"] = "将图片转换为 ICO 图标格式",
            ["图片格式转换"] = "图片格式批量转换",
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
                    if (ToolDescriptions.TryGetValue(child.Label, out var desc))
                        child.Description = desc;
                }
                ToolGroups.Add(category);
            }
        }

        [RelayCommand]
        private void NavigateToTool(PageNavigationItem item)
        {
            NavigateToPage?.Invoke(item);
        }
    }
}
