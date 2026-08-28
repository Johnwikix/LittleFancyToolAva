using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using FancyToolAva.ViewModels;

namespace FancyToolAva.Views;

public partial class VideoTranscodeView : UserControl
{
    private VideoTranscodeViewModel? _vm;
    private Avalonia.Controls.TabControl? _tabs;

    public VideoTranscodeView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _tabs ??= this.FindControl<Avalonia.Controls.TabControl>("ParamTabs");
        SyncTabForContainer();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as VideoTranscodeViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
        _tabs ??= this.FindControl<Avalonia.Controls.TabControl>("ParamTabs");
        SyncTabForContainer();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoTranscodeViewModel.SelectedContainer) || e.PropertyName == nameof(VideoTranscodeViewModel.IsGifMode))
            Avalonia.Threading.Dispatcher.UIThread.Post(SyncTabForContainer);
    }

    private void SyncTabForContainer()
    {
        if (_tabs == null) _tabs = this.FindControl<Avalonia.Controls.TabControl>("ParamTabs");
        if (_tabs == null || _vm == null) return;
        bool isGif = _vm.IsGifMode;
        // Tab order: 0 封装(常驻), 1 音频(常驻禁用), 2 画质, 3 滤镜, 4 GIF
        // 封装常驻后 GIF 模式不再需要强制切走；仅处理隐藏/禁用边界
        if (isGif)
        {
            if (_tabs.SelectedIndex is 2)
                _tabs.SelectedIndex = 4;
            else if (_tabs.SelectedIndex == 1)
                _tabs.SelectedIndex = 0; // 音频禁用时切到封装
            else if (_tabs.SelectedIndex < 0)
                _tabs.SelectedIndex = 4;
            // 0封装/3滤镜/4GIF 均保持
        }
        else
        {
            if (_tabs.SelectedIndex == 4)
                _tabs.SelectedIndex = 0;
            else if (_tabs.SelectedIndex < 0)
                _tabs.SelectedIndex = 0;
        }
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
