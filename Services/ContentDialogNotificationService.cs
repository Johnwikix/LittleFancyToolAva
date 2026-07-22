using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Logging;
using Serilog;

namespace LittleFancyToolAva.Services
{
    public class ContentDialogNotificationService : INotificationService
    {
        public void ShowError(string message) => Show(message, "错误", Log.Error);
        public void ShowSuccess(string message) => Show(message, "成功", Log.Information);
        public void ShowInfo(string message) => Show(message, "提示", Log.Information);
        public void ShowWarn(string message) => Show(message, "警告", Log.Warning);

        private static async void Show(string message, string title, Action<string, object[]> logAction)
        {
            logAction("UI notification: {Title} - {Message}", [title, message]);
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
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                Log.Debug(ex, "Notification dialog failed: {Title} - {Message}", title, message);
            }
        }
    }
}
