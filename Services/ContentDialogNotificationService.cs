using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FluentAvalonia.UI.Controls;
using System;

namespace LittleFancyToolAva.Services
{
    public class ContentDialogNotificationService : INotificationService
    {
        public void ShowError(string message) => Show(message, "错误");
        public void ShowSuccess(string message) => Show(message, "成功");
        public void ShowInfo(string message) => Show(message, "提示");
        public void ShowWarn(string message) => Show(message, "警告");

        private static async void Show(string message, string title)
        {
            try
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                    && desktop.MainWindow is Window window)
                {
                    var dialog = new FAContentDialog
                    {
                        Title = title,
                        Content = new TextBlock
                        {
                            Text = message,
                            Margin = new(20),
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        },
                        CloseButtonText = "确定",
                        DefaultButton = FAContentDialogButton.Close,
                    };
                    await dialog.ShowAsync();
                }
            }
            catch
            {
                // 静默失败——错误弹框本身出错时不再递归
            }
        }
    }
}