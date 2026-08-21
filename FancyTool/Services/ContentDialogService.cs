using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using System.Threading.Tasks;

namespace FancyToolAva.Services
{
    public class ContentDialogService : IDialogService
    {
        public async Task<TResult?> ShowDialogAsync<TResult>(object viewModel) where TResult : class
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow is Window window)
            {
                var dialog = new Window
                {
                    Title = LocalizationRegistry.Get("Dialog.Title_Default"),
                    Content = viewModel,
                    Width = 450,
                    Height = 400,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    SizeToContent = SizeToContent.WidthAndHeight,
                };
                var tcs = new TaskCompletionSource<TResult?>();
                dialog.Closed += (_, _) =>
                {
                    tcs.TrySetResult(viewModel as TResult);
                };
                dialog.KeyDown += (_, e) =>
                {
                    if (e.Key == Avalonia.Input.Key.Escape)
                    {
                        tcs.TrySetResult(null);
                        dialog.Close();
                    }
                    if (e.Key == Avalonia.Input.Key.Enter)
                    {
                        tcs.TrySetResult(viewModel as TResult);
                        dialog.Close();
                    }
                };
                await dialog.ShowDialog(window);
                return await tcs.Task;
            }
            return null;
        }
    }
}
