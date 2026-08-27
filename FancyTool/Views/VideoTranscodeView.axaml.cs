using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using FancyToolAva.ViewModels;

namespace FancyToolAva.Views;

public partial class VideoTranscodeView : UserControl
{
    public VideoTranscodeView()
    {
        InitializeComponent();
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.DragEffects = CanAccept(e) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = CanAccept(e) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (!CanAccept(e)) return;
        if (DataContext is not VideoTranscodeViewModel vm) return;
        if (vm.IsBusy) return;

        var paths = ExtractPaths(e);
        if (paths.Count > 0)
            vm.AddDroppedPaths(paths);
    }

    private bool CanAccept(DragEventArgs e)
    {
        if (DataContext is VideoTranscodeViewModel vm && vm.IsBusy) return false;
        foreach (var item in e.DataTransfer.Items)
        {
            if (!item.Contains(DataFormat.File)) continue;
            var storageItem = item.TryGetFile();
            if (storageItem == null) continue;
            var path = storageItem.Path.LocalPath;
            if (Uri.TryCreate(storageItem.Path.ToString(), UriKind.Absolute, out var uri))
            {
                try { path = uri.LocalPath; } catch { path = storageItem.Path.ToString(); }
            }
            // Fallback to TryGetLocalPath via extension if available
            try
            {
                var extPath = storageItem.TryGetLocalPath();
                if (!string.IsNullOrEmpty(extPath)) path = extPath;
            }
            catch { }
            if (!string.IsNullOrEmpty(path))
            {
                if (Directory.Exists(path)) return true;
                if (File.Exists(path)) return true;
            }
        }
        return false;
    }

    private static List<string> ExtractPaths(DragEventArgs e)
    {
        var paths = new List<string>();
        foreach (var item in e.DataTransfer.Items)
        {
            var storageItem = item.TryGetFile();
            if (storageItem == null) continue;
            string? path = null;
            try { path = storageItem.TryGetLocalPath(); } catch { }
            if (string.IsNullOrEmpty(path))
            {
                try { path = new Uri(storageItem.Path.ToString()).LocalPath; } catch { path = storageItem.Path.ToString(); }
            }
            if (!string.IsNullOrEmpty(path))
                paths.Add(path);
        }
        return paths;
    }
}
