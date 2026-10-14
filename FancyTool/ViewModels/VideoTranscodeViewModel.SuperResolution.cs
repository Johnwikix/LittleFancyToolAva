using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Services;

namespace FancyToolAva.ViewModels;

public partial class VideoTranscodeViewModel
{
    private readonly IVideoSuperResolutionService? _videoSrService;
    private CancellationTokenSource? _srInstallCts;
    private static readonly SuperResolutionModel[] VideoModels = [SuperResolutionModel.SpanClearRealityV1, SuperResolutionModel.LiveActionSpanX2, SuperResolutionModel.RealEsrganGeneralX4V3, SuperResolutionModel.RealEsrganX4PlusAnime, SuperResolutionModel.RealEsrganX4Plus];

    /// <summary>Gets or sets whether video frames are enhanced with a neural model.</summary>
    public bool IsVideoSrEnabled
    {
        get => field;
        set { if (IsBusy) return; if (SetProperty(ref field, value)) RefreshSrState(); }
    }
    /// <summary>Gets the localized video model choices.</summary>
    public IReadOnlyList<LocalizedOption> VideoSrModelLabels { get; } = [new("VideoTranscode.Sr_ModelSpan"),
        new("ImageConvert.SrModel_LiveActionSpanX2"), new("VideoTranscode.Sr_ModelFast"),
        new("VideoTranscode.Sr_ModelAnime"), new("VideoTranscode.Sr_ModelQuality")];
    /// <summary>Gets the localized accelerator preferences.</summary>
    public IReadOnlyList<LocalizedOption> VideoSrBackendLabels { get; } = [new("VideoTranscode.Sr_BackendAuto"),
        new("VideoTranscode.Sr_BackendNpu"), new("VideoTranscode.Sr_BackendGpu"), new("VideoTranscode.Sr_BackendCpu")];
    private static readonly string[] VideoSrScales = ["2×", "4×"];
    private static readonly string[] VideoSrNativeScales = ["2×"];
    /// <summary>Gets the available enhancement scales.</summary>
    public string[] VideoSrScaleLabels => VideoModels[VideoSrModelIndex] == SuperResolutionModel.LiveActionSpanX2 ? VideoSrNativeScales : VideoSrScales;
    /// <summary>Gets or sets the selected neural model index.</summary>
    public int VideoSrModelIndex
    {
        get => field;
        set
        {
            if (IsBusy || value < 0) return;
            if (SetProperty(ref field, Math.Clamp(value, 0, VideoModels.Length - 1)))
            {
                if (VideoModels[field] == SuperResolutionModel.LiveActionSpanX2) VideoSrScaleIndex = 0;
                OnPropertyChanged(nameof(VideoSrScaleLabels));
                OnPropertyChanged(nameof(VideoSrScaleIndex));
                RefreshSrState();
            }
        }
    }
    /// <summary>Gets or sets the enhancement scale index.</summary>
    public int VideoSrScaleIndex { get => field; set { if (!IsBusy && value >= 0) SetProperty(ref field, Math.Clamp(value, 0, VideoModels[VideoSrModelIndex] == SuperResolutionModel.LiveActionSpanX2 ? 0 : 1)); } }
    /// <summary>Gets or sets the hardware preference index.</summary>
    public int VideoSrBackendIndex { get => field; set { if (!IsBusy && value >= 0) SetProperty(ref field, Math.Clamp(value, 0, 3)); } }
    /// <summary>Gets or sets whether software inference is allowed after hardware failure.</summary>
    public bool VideoSrAllowCpuFallback { get => field; set { if (!IsBusy) SetProperty(ref field, value); } } = true;
    /// <summary>Gets the actual inference device once a video conversion starts.</summary>
    public string VideoSrDeviceText { get => field; private set => SetProperty(ref field, value); } = string.Empty;
    /// <summary>Gets whether the selected model is being installed.</summary>
    public bool IsSrInstalling { get => field; private set { if (SetProperty(ref field, value)) RefreshSrState(); } }
    /// <summary>Gets whether compatible hardware providers are being installed.</summary>
    public bool IsSrHardwareInstalling { get => field; private set => SetProperty(ref field, value); }
    /// <summary>Gets the normalized download progress.</summary>
    public double SrDownloadProgress { get => field; private set => SetProperty(ref field, value); }
    /// <summary>Gets the installation status message.</summary>
    public string SrDownloadText { get => field; private set => SetProperty(ref field, value); } = string.Empty;
    /// <summary>Gets whether the selected model exists in the shared model folder.</summary>
    public bool IsVideoSrModelReady => _videoSrService?.IsModelAvailable(VideoModels[VideoSrModelIndex]) == true;
    /// <summary>Gets the localized installer button label.</summary>
    public string SrInstallLabel => LocalizationRegistry.Get(IsVideoSrModelReady ? "VideoTranscode.Sr_VerifyModel" : "VideoTranscode.Sr_InstallModel");

    private void RefreshSrState()
    {
        OnPropertyChanged(nameof(IsVideoSrModelReady));
        OnPropertyChanged(nameof(SrInstallLabel));
        InstallVideoSrModelCommand.NotifyCanExecuteChanged();
        InstallVideoSrAccelerationCommand.NotifyCanExecuteChanged();
        StartTranscodeCommand.NotifyCanExecuteChanged();
    }
    private bool CanInstallVideoSr() => !IsBusy && !IsSrInstalling && _videoSrService != null;

    [RelayCommand(CanExecute = nameof(CanInstallVideoSr))]
    private async Task InstallVideoSrModel()
    {
        if (_videoSrService == null) return;
        IsSrInstalling = true;
        using var cancellation = _srInstallCts = new CancellationTokenSource();
        try
        {
            await _videoSrService.EnsureModelAsync(VideoModels[VideoSrModelIndex], cancellation.Token);
            SrDownloadText = LocalizationRegistry.Get("VideoTranscode.Sr_ModelReady");
        }
        catch (OperationCanceledException) { SrDownloadText = LocalizationRegistry.Get("VideoTranscode.Sr_InstallCanceled"); }
        catch (Exception ex) { SrDownloadText = ex.Message; _notificationService.ShowError(ex.Message); }
        finally { _srInstallCts = null; if (!_isDisposed) IsSrInstalling = false; }
    }

    [RelayCommand(CanExecute = nameof(CanInstallVideoSr))]
    private async Task InstallVideoSrAcceleration()
    {
        if (_videoSrService == null) return;
        IsSrHardwareInstalling = IsSrInstalling = true;
        using var cancellation = _srInstallCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            SrDownloadText = LocalizationRegistry.Get("VideoTranscode.Sr_InstallingHardware");
            await _videoSrService.InstallAccelerationAsync(cancellation.Token);
            SrDownloadText = LocalizationRegistry.Get("VideoTranscode.Sr_HardwareReady");
        }
        catch (OperationCanceledException) { SrDownloadText = LocalizationRegistry.Get("VideoTranscode.Sr_InstallCanceled"); }
        catch (Exception ex) { SrDownloadText = ex.Message; if (!_isDisposed) _notificationService.ShowError(ex.Message); }
        finally { _srInstallCts = null; if (!_isDisposed) { IsSrInstalling = false; IsSrHardwareInstalling = false; } }
    }

    [RelayCommand]
    private void CancelSrInstallation() => _srInstallCts?.Cancel();

    private void OnSrDownloadChanged(ModelDownloadProgress progress) => Dispatcher.UIThread.Post(() =>
    {
        if (_isDisposed) return;
        SrDownloadProgress = progress.TotalBytes is > 0 ? Math.Clamp(progress.BytesDownloaded / (double)progress.TotalBytes, 0, 1) : 0;
        SrDownloadText = progress.Stage == ModelDownloadStage.Done ? LocalizationRegistry.Get("VideoTranscode.Sr_ModelReady")
            : progress.Message ?? $"{progress.FileName} · {progress.BytesDownloaded / 1048576.0:0.0} MB";
        RefreshSrState();
    });

    private void OnSrDeviceChanged(VideoSrDevice device) => Dispatcher.UIThread.Post(() =>
    {
        if (_isDisposed) return;
        VideoSrDeviceText = device.Name + (device.FallbackReason != null ? " · " + LocalizationRegistry.Get("VideoTranscode.Sr_Fallback") : "");
    });
}
