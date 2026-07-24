using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using LittleFancyToolAva.ViewModels;

namespace LittleFancyToolAva.Views;

public partial class FileEncryptionView : UserControl
{
    public FileEncryptionView()
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

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!CanAccept(e)) return;
        if (DataContext is not FileEncryptionViewModel vm) return;
        if (vm.IsBusy) return;

        var paths = ExtractPaths(e);
        if (paths.Count > 0)
            vm.AddDroppedPaths(paths);
    }

    private bool CanAccept(DragEventArgs e)
    {
        if (DataContext is FileEncryptionViewModel vm && vm.IsBusy) return false;
        foreach (var item in e.DataTransfer.Items)
        {
            if (item.Contains(DataFormat.File))
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
}