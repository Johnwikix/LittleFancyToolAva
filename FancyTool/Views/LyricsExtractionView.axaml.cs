using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using FancyToolAva.ViewModels;

namespace FancyToolAva.Views;

public partial class LyricsExtractionView : UserControl
{
    public LyricsExtractionView() => InitializeComponent();
    private void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = DataContext is LyricsExtractionViewModel { IsIdle: true } && e.DataTransfer.Items.Any(i => i.Contains(DataFormat.File)) ? DragDropEffects.Copy : DragDropEffects.None;
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not LyricsExtractionViewModel { IsIdle: true } vm) return;
        var paths = e.DataTransfer.Items.Select(i => i.TryGetFile()?.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToArray();
        if (paths.Length > 0) await vm.AddPathsAsync(paths);
    }
}
