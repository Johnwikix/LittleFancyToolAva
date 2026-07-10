using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is Window window)
            {
                var dialog = new Window
                {
                    Title = title,
                    Content = new TextBlock { Text = message, Margin = new(20), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    Width = 400,
                    Height = 200,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    SizeToContent = SizeToContent.WidthAndHeight,
                };
                dialog.KeyDown += (_, e) =>
                {
                    if (e.Key == Avalonia.Input.Key.Escape) dialog.Close();
                };
                await dialog.ShowDialog(window);
            }
        }
    }
}
