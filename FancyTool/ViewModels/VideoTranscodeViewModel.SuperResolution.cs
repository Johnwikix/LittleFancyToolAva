using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Services;

namespace FancyToolAva.ViewModels;

public partial class VideoTranscodeViewModel
{
    private readonly IVideoSuperResolutionService? _videoSrService;
    private CancellationTokenSource? _srInstallCts;
    private static readonly VideoSrModel[] VideoModels = VideoSrModelCatalog.Models.Select(model => model.Model).ToArray();
    private static readonly VideoSrBackend[] VideoBackends = [VideoSrBackend.Auto];

    /// <summary>Gets or sets whether video frames are enhanced with a neural model.</summary>
    public bool IsVideoSrEnabled
    {
        get => field;
        set { if (IsBusy) return; if (SetProperty(ref field, value)) RefreshSrState(); }
    }
    /// <summary>受限时保留已保存选项，并允许取消勾选以继续普通转码。</summary>
    public bool CanToggleVideoSr => !IsBusy && (IsVideoSrEnabled
        || _licenseService.CanUseFeature(LicenseFeature.VideoSuperResolution));
    /// <summary>Gets the localized video model choices.</summary>
    public IReadOnlyList<LocalizedOption> VideoSrModelLabels { get; } = VideoSrModelCatalog.Models.Select(model => new LocalizedOption(model.LabelKey)).ToArray();
    /// <summary>Gets the localized accelerator preferences.</summary>
    public IReadOnlyList<LocalizedOption> VideoSrBackendLabels { get; } = [new("VideoTranscode.Sr_AutoGpu")];
    private static readonly string[] VideoSrScales = ["2×", "4×"];
    /// <summary>Gets the available enhancement scales.</summary>
    public string[] VideoSrScaleLabels => VideoSrScales;
    /// <summary>Gets or sets the selected neural model index.</summary>
    public int VideoSrModelIndex
    {
        get => field;
        set
        {
            if (IsBusy || value < 0) return;
            if (SetProperty(ref field, Math.Clamp(value, 0, VideoModels.Length - 1)))
            {
                if (!SupportsVideoSrFp16) VideoSrUseFp16 = false;
                if (IsVideoSrVendorOnly) VideoSrBackendIndex = 0;
                OnPropertyChanged(nameof(IsVideoSrVendorOnly));
                OnPropertyChanged(nameof(SupportsVideoSrFp16));
                OnPropertyChanged(nameof(IsVideoSrRavu));
                OnPropertyChanged(nameof(VideoSrScaleLabels));
                OnPropertyChanged(nameof(VideoSrScaleIndex));
                RefreshSrState();
            }
        }
    }
    /// <summary>Gets or sets the enhancement scale index.</summary>
    public int VideoSrScaleIndex { get => field; set { if (!IsBusy && value >= 0) SetProperty(ref field, Math.Clamp(value, 0, 1)); } }
    /// <summary>Gets or sets the hardware preference index.</summary>
    public int VideoSrBackendIndex
    {
        get => field;
        set
        {
            if (IsBusy || value < 0) return;
            if (!SetProperty(ref field, IsVideoSrVendorOnly ? 0 : Math.Clamp(value, 0, VideoBackends.Length - 1))) return;
            if (!SupportsVideoSrFp16) VideoSrUseFp16 = false;
            OnPropertyChanged(nameof(SupportsVideoSrFp16));
        }
    }
    /// <summary>Gets whether the selected model has a validated half-precision graph.</summary>
    public bool SupportsVideoSrFp16 => VideoSrBackendIndex == 0 && VideoSrModelCatalog.Get(VideoModels[VideoSrModelIndex]).SupportsFp16;
    /// <summary>Gets whether RAVU requires full-precision DirectML execution.</summary>
    public bool IsVideoSrRavu => !VideoSrModelCatalog.Get(VideoModels[VideoSrModelIndex]).SupportsFp16;
    /// <summary>Gets whether the selected model requires a vendor GPU without DirectML fallback.</summary>
    public bool IsVideoSrVendorOnly => VideoSrModelCatalog.Get(VideoModels[VideoSrModelIndex]).RequiresVendorGpu;
    /// <summary>Gets or sets whether half-precision GPU acceleration is enabled.</summary>
    public bool VideoSrUseFp16 { get => field; set { if (!IsBusy) SetProperty(ref field, value && SupportsVideoSrFp16); } } = true;
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
        OnPropertyChanged(nameof(CanToggleVideoSr));
        OnPropertyChanged(nameof(ShowInterpolationOrder));
        OnPropertyChanged(nameof(IsVideoSrModelReady));
        OnPropertyChanged(nameof(SrInstallLabel));
        InstallVideoSrModelCommand.NotifyCanExecuteChanged();
        InstallVideoSrAccelerationCommand.NotifyCanExecuteChanged();
        StartTranscodeCommand.NotifyCanExecuteChanged();
    }
    private bool CanInstallVideoSr() => !_isDisposed && !IsBusy && !IsSrInstalling && _videoSrService != null
        && _licenseService.CanUseFeature(LicenseFeature.VideoSuperResolution);

    [RelayCommand(CanExecute = nameof(CanInstallVideoSr))]
    private async Task InstallVideoSrModel()
    {
        if (_videoSrService == null || _isDisposed || IsBusy || IsSrInstalling
            || !await EnsureAiAccessAsync(LicenseFeature.VideoSuperResolution)) return;
        if (_isDisposed || IsBusy || IsSrInstalling) return;
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
        if (_videoSrService == null || _isDisposed || IsBusy || IsSrInstalling
            || !await EnsureAiAccessAsync(LicenseFeature.VideoSuperResolution)) return;
        if (_isDisposed || IsBusy || IsSrInstalling) return;
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
