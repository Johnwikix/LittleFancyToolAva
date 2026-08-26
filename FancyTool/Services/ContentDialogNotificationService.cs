using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FluentAvalonia.UI.Controls;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging;
using Serilog;

namespace FancyToolAva.Services
{
    public class ContentDialogNotificationService : INotificationService
    {
        public void ShowError(string message) => Show(message, LocalizationRegistry.Get("Dialog.Title_Error"), Log.Error);
        public void ShowSuccess(string message) => Show(message, LocalizationRegistry.Get("Dialog.Title_Success"), Log.Information);
        public void ShowInfo(string message) => Show(message, LocalizationRegistry.Get("Dialog.Title_Info"), Log.Information);
        public void ShowWarn(string message) => Show(message, LocalizationRegistry.Get("Dialog.Title_Warning"), Log.Warning);

        private static async void Show(string message, string title, Action<string, object[]> logAction)
        {
            logAction("UI notification: {Title} - {Message}", [title, message]);
            // Ensure dialog is shown on UI thread
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess() == false)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Show(message, title, logAction));
                return;
            }
            try
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                    && desktop.MainWindow is Window window)
                {
                    var dialog = new FAContentDialog
                    {
                        Title = title,
                        Content = new SelectableTextBlock
                        {
                            Text = message,
                            Margin = new(20),
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        },
                        CloseButtonText = LocalizationRegistry.Get("Common.Button_OK"),
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
