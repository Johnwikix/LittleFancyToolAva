using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using FancyToolAva.ViewModels;

namespace FancyToolAva.Views;

public partial class Img2Base64View : UserControl
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff"
    };

    public Img2Base64View()
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
        if (DataContext is not Img2Base64ViewModel vm) return;
        if (vm.IsBusy) return;

        var paths = ExtractPaths(e);
        string? firstImage = paths.FirstOrDefault(IsImageFile);
        if (firstImage == null) return;

        await vm.LoadImageFromPathAsync(firstImage);
    }

    private bool CanAccept(DragEventArgs e)
    {
        if (DataContext is Img2Base64ViewModel vm && vm.IsBusy) return false;
        foreach (var item in e.DataTransfer.Items)
        {
            if (!item.Contains(DataFormat.File)) continue;
            var storageItem = item.TryGetFile();
            var path = storageItem?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path) && IsImageFile(path))
                return true;
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
            var path = storageItem.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                paths.Add(path);
        }
        return paths;
    }

    private static bool IsImageFile(string path)
        => ImageExtensions.Contains(Path.GetExtension(path));
}