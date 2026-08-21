using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;

namespace FancyToolAva.Utils
{
    public static class ClipboardHelper
    {
        public static async Task SetTextAsync(string text)
        {
            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.Clipboard is { } clipboard)
            {
                var dataTransfer = new DataTransfer();
                dataTransfer.Add(DataTransferItem.CreateText(text));
                await clipboard.SetDataAsync(dataTransfer);
            }
        }
    }
}
