using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Services;

namespace FancyToolAva.ViewModels;

public partial class VideoTranscodeViewModel
{
    private readonly IVideoSuperResolutionService? _videoSrService;
    private CancellationTokenSource? _srInstallCts;
    private static readonly SuperResolutionModel[] VideoModels = [SuperResolutionModel.RealEsrganGeneralX4V3, SuperResolutionModel.RealEsrganX4PlusAnime, SuperResolutionModel.RealEsrganX4Plus];

    /// <summary>Gets or sets whether video frames are enhanced with a neural model.</summary>
    public bool IsVideoSrEnabled
    {
        get => field;
        set { if (IsBusy) return; if (SetProperty(ref field, value)) RefreshSrState(); }
    }
    /// <summary>Gets the localized video model choices.</summary>
    public string[] VideoSrModelLabels => [LocalizationRegistry.Get("VideoTranscode.Sr_ModelFast"), LocalizationRegistry.Get("VideoTranscode.Sr_ModelAnime"), LocalizationRegistry.Get("VideoTranscode.Sr_ModelQuality")];
    /// <summary>Gets the localized accelerator preferences.</summary>
    public string[] VideoSrBackendLabels => [LocalizationRegistry.Get("VideoTranscode.Sr_BackendAuto"), LocalizationRegistry.Get("VideoTranscode.Sr_BackendNpu"), LocalizationRegistry.Get("VideoTranscode.Sr_BackendGpu"), "CPU"];
    /// <summary>Gets the available enhancement scales.</summary>
    public string[] VideoSrScaleLabels { get; } = ["2×", "4×"];
    /// <summary>Gets or sets the selected neural model index.</summary>
    public int VideoSrModelIndex { get => field; set { if (IsBusy || value < 0) return; if (SetProperty(ref field, Math.Clamp(value, 0, 2))) RefreshSrState(); } }
    /// <summary>Gets or sets the enhancement scale index.</summary>
    public int VideoSrScaleIndex { get => field; set { if (!IsBusy && value >= 0) SetProperty(ref field, Math.Clamp(value, 0, 1)); } }
    /// <summary>Gets or sets the hardware preference index.</summary>
    public int VideoSrBackendIndex { get => field; set { if (!IsBusy && value >= 0) SetProperty(ref field, Math.Clamp(value, 0, 3)); } }
    /// <summary>Gets or sets whether software inference is allowed after hardware failure.</summary>
    public bool VideoSrAllowCpuFallback { get => field; set { if (!IsBusy) SetProperty(ref field, value); } } = true;
    /// <summary>Gets the actual inference device once a video conversion starts.</summary>
    public string VideoSrDeviceText { get => field; private set => SetProperty(ref field, value); } = string.Empty;
    /// <summary>Gets whether the selected model is being installed.</summary>
    public bool IsSrInstalling { get => field; private set { if (SetProperty(ref field, value)) RefreshSrState(); } }
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
        catch (OperationCanceledException) when (_isDisposed) { }
        catch (Exception ex) { SrDownloadText = ex.Message; _notificationService.ShowError(ex.Message); }
        finally { _srInstallCts = null; if (!_isDisposed) IsSrInstalling = false; }
    }

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
