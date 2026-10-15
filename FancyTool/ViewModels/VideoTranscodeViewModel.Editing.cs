using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.ViewModels;

public partial class VideoTranscodeViewModel
{
    private readonly SemaphoreSlim _previewGate = new(1);
    private CancellationTokenSource? _previewCts;
    private bool _refreshingEditor;
    private string? _previewError;
    private ProgressPreviewTarget? _progressPreviewTarget;
    private bool _previewVisible;

    private sealed record ProgressPreviewTarget(VideoFileItem Item, VideoPreviewBuffer Buffer);

    /// <summary>Signals that pixels in the reusable preview bitmap have changed.</summary>
    internal event Action? PreviewFrameUpdated;

    /// <summary>Gets the decoded image for the selected source position.</summary>
    public WriteableBitmap? PreviewBitmap { get => field; private set => SetProperty(ref field, value); }

    /// <summary>Gets whether a decoded frame is available.</summary>
    public bool HasPreview => PreviewBitmap != null;

    /// <summary>Gets whether a source frame is being decoded.</summary>
    public bool IsPreviewLoading { get => field; private set { if (SetProperty(ref field, value)) OnPropertyChanged(nameof(PreviewStatusText)); } }

    /// <summary>Gets the source duration in seconds.</summary>
    public double SourceDurationSeconds => SelectedFileItem?.ProbeInfo is { HasVideo: true } probe ? probe.DurationMs / 1000.0 : 0;

    /// <summary>Gets the upper bound of the trim slider, including an empty-source fallback.</summary>
    public double TimelineMaximum => Math.Max(0.001, SourceDurationSeconds);

    /// <summary>Gets the minimum selection length, corresponding to one source frame.</summary>
    public double MinimumTrimRange => Math.Min(TimelineMaximum,
        1 / (SelectedFileItem?.ProbeInfo?.Fps is > 0 and var fps ? Math.Max(1, fps) : 30));

    /// <summary>Gets whether the selected file has a known video duration and can be edited.</summary>
    public bool CanEditTrim => !IsBusy && SourceDurationSeconds > 0;

    /// <summary>Gets or sets the inclusive trim start for the selected file.</summary>
    public double TrimStartSeconds
    {
        get => SelectedFileItem?.TrimStartSeconds ?? 0;
        set
        {
            if (_refreshingEditor || !CanEditTrim || !double.IsFinite(value) || SelectedFileItem is not { } item) return;
            double start = Math.Clamp(value, 0, Math.Max(0, TrimEndSeconds - MinimumTrimRange));
            if (Math.Abs(start - item.TrimStartSeconds) < 0.000001) return;
            item.TrimStartSeconds = start;
            RefreshTrimProperties();
            RequestPreview(start);
        }
    }

    /// <summary>Gets or sets the exclusive trim end for the selected file.</summary>
    public double TrimEndSeconds
    {
        get => SelectedFileItem?.TrimEndSeconds ?? SourceDurationSeconds;
        set
        {
            if (_refreshingEditor || !CanEditTrim || !double.IsFinite(value) || SelectedFileItem is not { } item) return;
            double end = Math.Clamp(value, Math.Min(SourceDurationSeconds, TrimStartSeconds + MinimumTrimRange), SourceDurationSeconds);
            if (Math.Abs(end - TrimEndSeconds) < 0.000001) return;
            item.TrimEndSeconds = Math.Abs(end - SourceDurationSeconds) < 0.000001 ? null : end;
            RefreshTrimProperties();
            RequestPreview(EndPreviewPosition);
        }
    }

    private double EndPreviewPosition => Math.Max(TrimStartSeconds, TrimEndSeconds - MinimumTrimRange);

    /// <summary>Gets the source timestamp displayed below the preview.</summary>
    public double PreviewPositionSeconds { get => field; private set { if (SetProperty(ref field, value)) OnPropertyChanged(nameof(PreviewPositionText)); } }

    /// <summary>Gets the formatted source timestamp of the preview.</summary>
    public string PreviewPositionText => FormatVideoTime(PreviewPositionSeconds);

    /// <summary>Gets the formatted original source duration.</summary>
    public string SourceDurationText => FormatVideoTime(SourceDurationSeconds);

    /// <summary>Gets the formatted duration of the selected clip.</summary>
    public string TrimDurationText => FormatVideoTime(Math.Max(0, TrimEndSeconds - TrimStartSeconds));

    /// <summary>Gets the current source's conversion progress.</summary>
    public double PreviewConversionProgress => SelectedFileItem?.Progress ?? 0;

    /// <summary>Gets whether the preview follows an active conversion.</summary>
    public bool IsProgressPreview => IsBusy && SelectedFileItem?.Status == FileStatus.Converting;

    /// <summary>Gets the localized preview mode label.</summary>
    public string PreviewModeText => LocalizationRegistry.Get(IsProgressPreview ? "VideoTranscode.Preview_Converting" : "VideoTranscode.Preview_Source");

    /// <summary>Gets the localized empty, loading, or failure message for the preview.</summary>
    public string PreviewStatusText => LocalizationRegistry.Get(SelectedFileItem == null ? "VideoTranscode.Preview_Empty"
        : IsPreviewLoading ? "VideoTranscode.Preview_Loading"
        : _previewError != null ? "VideoTranscode.Preview_Failed" : "VideoTranscode.Preview_NoFrame");

    /// <summary>Gets the name of the source shown in the editing panel.</summary>
    public string PreviewFileName => SelectedFileItem?.FileName ?? LocalizationRegistry.Get("VideoTranscode.Label_Preview");

    private static string FormatVideoTime(double seconds) => TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, seconds) * 1000)).ToString(@"hh\:mm\:ss\.fff");

    private void OnFileItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SelectedFileItem == null || !FileItems.Contains(SelectedFileItem)) SelectedFileItem = FileItems.FirstOrDefault();
        InvalidateStartTranscode();
    }

    private void OnSelectedItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoFileItem.ProbeInfo)) RefreshEditor();
        if (e.PropertyName is nameof(VideoFileItem.Status) or nameof(VideoFileItem.Progress))
        {
            OnPropertyChanged(nameof(IsProgressPreview));
            OnPropertyChanged(nameof(PreviewModeText));
            OnPropertyChanged(nameof(PreviewConversionProgress));
        }
    }

    private void RefreshTrimProperties()
    {
        _refreshingEditor = true;
        try
        {
            OnPropertyChanged(nameof(TrimEndSeconds));
            OnPropertyChanged(nameof(TrimStartSeconds));
            OnPropertyChanged(nameof(TrimDurationText));
        }
        finally { _refreshingEditor = false; }
    }

    private void RefreshEditor()
    {
        _previewCts?.Cancel();
        Interlocked.Exchange(ref _progressPreviewTarget, SelectedFileItem is { } selected
            ? new ProgressPreviewTarget(selected, new VideoPreviewBuffer()) : null)?.Buffer.Dispose();
        _previewError = null;
        ReplacePreview(null);
        IsPreviewLoading = false;
        _refreshingEditor = true;
        try
        {
            OnPropertyChanged(nameof(SourceDurationSeconds));
            OnPropertyChanged(nameof(TimelineMaximum));
            OnPropertyChanged(nameof(MinimumTrimRange));
            OnPropertyChanged(nameof(SourceDurationText));
            OnPropertyChanged(nameof(TrimEndSeconds));
            OnPropertyChanged(nameof(TrimStartSeconds));
            OnPropertyChanged(nameof(TrimDurationText));
            OnPropertyChanged(nameof(PreviewFileName));
            OnPropertyChanged(nameof(PreviewStatusText));
            OnPropertyChanged(nameof(PreviewModeText));
            OnPropertyChanged(nameof(IsProgressPreview));
            OnPropertyChanged(nameof(PreviewConversionProgress));
        }
        finally { _refreshingEditor = false; }
        RefreshEditingAvailability();
        PreviewPositionSeconds = TrimStartSeconds;
        if (SelectedFileItem?.ProbeInfo?.HasVideo == true && (!IsBusy || SelectedFileItem.Status != FileStatus.Converting)) RequestPreview(TrimStartSeconds);
    }

    private void RefreshEditingAvailability()
    {
        OnPropertyChanged(nameof(CanEditTrim));
        OnPropertyChanged(nameof(IsProgressPreview));
        OnPropertyChanged(nameof(PreviewModeText));
        ResetTrimCommand.NotifyCanExecuteChanged();
        PreviewStartCommand.NotifyCanExecuteChanged();
        PreviewEndCommand.NotifyCanExecuteChanged();
        RefreshSrState();
        RefreshInterpolationState();
    }

    private bool CanPreviewBoundary() => CanEditTrim;

    [RelayCommand(CanExecute = nameof(CanPreviewBoundary))]
    private void PreviewStart() => RequestPreview(TrimStartSeconds);

    [RelayCommand(CanExecute = nameof(CanPreviewBoundary))]
    private void PreviewEnd() => RequestPreview(EndPreviewPosition);

    [RelayCommand(CanExecute = nameof(CanPreviewBoundary))]
    private void ResetTrim()
    {
        if (SelectedFileItem is not { } item) return;
        item.TrimStartSeconds = 0;
        item.TrimEndSeconds = null;
        RefreshTrimProperties();
        RequestPreview(0);
    }

    private void RequestPreview(double position)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        var cts = _previewCts = new CancellationTokenSource();
        if (SelectedFileItem is not { } item || _isDisposed) return;
        _previewError = null;
        IsPreviewLoading = true;
        _ = LoadPreviewAsync(item, position, cts.Token);
    }

    private async Task LoadPreviewAsync(VideoFileItem item, double position, CancellationToken ct)
    {
        bool entered = false;
        try
        {
            await Task.Delay(150, ct);
            await _previewGate.WaitAsync(ct);
            entered = true;
            var frame = await _videoService.GetPreviewAsync(item.FilePath, position, ct);
            if (!ct.IsCancellationRequested && !_isDisposed && ReferenceEquals(item, SelectedFileItem))
            {
                if (frame != null) ApplyPreview(frame);
                else ReplacePreview(null);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested && !_isDisposed && ReferenceEquals(item, SelectedFileItem))
            {
                _previewError = ex.Message;
                ReplacePreview(null);
                _loggerField.LogDebug(ex, "Video preview failed: {File}", item.FilePath);
            }
        }
        finally
        {
            if (entered) _previewGate.Release();
            if (!ct.IsCancellationRequested && !_isDisposed && ReferenceEquals(item, SelectedFileItem))
            {
                IsPreviewLoading = false;
                OnPropertyChanged(nameof(PreviewStatusText));
            }
        }
    }

    private void ApplyPreview(VideoPreviewFrame frame) => ApplyPreview(frame.Pixels, frame.Width, frame.Height, frame.PositionSeconds);

    private unsafe void ApplyPreview(ReadOnlySpan<byte> pixels, int width, int height, double position)
    {
        var bitmap = PreviewBitmap;
        bool replaced = bitmap == null || bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height;
        if (replaced) bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var buffer = bitmap.Lock())
        {
            int stride = checked(width * 4);
            if (buffer.RowBytes == stride) pixels[..(stride * height)].CopyTo(new Span<byte>((void*)buffer.Address, stride * height));
            else for (int row = 0; row < height; row++)
                pixels.Slice(row * stride, stride).CopyTo(new Span<byte>((void*)(buffer.Address + row * buffer.RowBytes), stride));
        }
        if (replaced) ReplacePreview(bitmap);
        PreviewPositionSeconds = position;
        _previewError = null;
        IsPreviewLoading = false;
        PreviewFrameUpdated?.Invoke();
    }

    private void ReplacePreview(WriteableBitmap? bitmap)
    {
        var old = PreviewBitmap;
        PreviewBitmap = bitmap;
        OnPropertyChanged(nameof(HasPreview));
        old?.Dispose();
    }

    internal void SetPreviewVisible(bool visible) => Volatile.Write(ref _previewVisible, visible);

    internal void RenderLatestPreview()
    {
        var target = Volatile.Read(ref _progressPreviewTarget);
        using var frame = target?.Buffer.TakeLatest();
        if (frame == null || _isDisposed || !ReferenceEquals(target?.Item, SelectedFileItem)) return;
        _previewCts?.Cancel();
        ApplyPreview(frame.Pixels, frame.Width, frame.Height, frame.PositionSeconds);
    }

    internal IProgress<VideoPreviewFrame> CreateProgressPreview(VideoFileItem item) => new ProgressPreviewSink(this, item);

    private sealed class ProgressPreviewSink(VideoTranscodeViewModel owner, VideoFileItem item) : IVideoPreviewSink
    {
        public bool WantsFrame => !owner._isDisposed && Volatile.Read(ref owner._previewVisible)
            && ReferenceEquals(Volatile.Read(ref owner._progressPreviewTarget)?.Item, item);

        public void Publish(ReadOnlySpan<byte> pixels, int width, int height, double positionSeconds)
        {
            var target = Volatile.Read(ref owner._progressPreviewTarget);
            if (WantsFrame && ReferenceEquals(target?.Item, item)) target.Buffer.Publish(pixels, width, height, positionSeconds);
        }

        public void Report(VideoPreviewFrame frame) => Publish(frame.Pixels, frame.Width, frame.Height, frame.PositionSeconds);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
