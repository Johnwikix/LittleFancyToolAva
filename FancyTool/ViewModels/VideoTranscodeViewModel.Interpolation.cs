using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Services;

namespace FancyToolAva.ViewModels;

public partial class VideoTranscodeViewModel
{
    private readonly IVideoInterpolationService? _interpolationService;
    private CancellationTokenSource? _interpolationInstallCts;

    /// <summary>Gets the localized interpolation methods.</summary>
    public IReadOnlyList<LocalizedOption> InterpolationModes { get; } = [new("VideoTranscode.Interp_Off"), new("VideoTranscode.Interp_Ai"), new("VideoTranscode.Interp_Motion")];
    /// <summary>Gets the localized RIFE model choices.</summary>
    public IReadOnlyList<LocalizedOption> InterpolationModels { get; } = [new("VideoTranscode.Interp_ModelLite"), new("VideoTranscode.Interp_ModelQuality")];
    /// <summary>Gets the output frame rate choices.</summary>
    public IReadOnlyList<LocalizedOption> InterpolationRates { get; } = [new("VideoTranscode.Interp_Double"), new("VideoTranscode.Interp_Quadruple"), new("VideoTranscode.Interp_Custom")];
    /// <summary>Gets the enhancement and interpolation order choices.</summary>
    public IReadOnlyList<LocalizedOption> InterpolationOrders { get; } = [new("VideoTranscode.Interp_OrderAuto"), new("VideoTranscode.Interp_OrderBefore"), new("VideoTranscode.Interp_OrderAfter")];
    /// <summary>Gets or sets the combined frame processing order.</summary>
    public int InterpolationOrderIndex { get; set { if (!IsBusy && value >= 0) SetProperty(ref field, Math.Clamp(value, 0, 2)); } }
    /// <summary>Gets whether the combined pipeline order control is visible.</summary>
    public bool ShowInterpolationOrder => IsInterpolationEnabled && IsVideoSrEnabled;
    /// <summary>Gets or sets the selected interpolation method.</summary>
    public int InterpolationModeIndex
    {
        get;
        set { if (!IsBusy && !IsInterpolationInstalling && value >= 0 && SetProperty(ref field, Math.Clamp(value, 0, 2))) { ResetPresetToCustom(); RefreshInterpolationState(); } }
    }
    /// <summary>Gets or sets the selected RIFE model.</summary>
    public int InterpolationModelIndex
    {
        get;
        set { if (!IsBusy && !IsInterpolationInstalling && value >= 0 && SetProperty(ref field, Math.Clamp(value, 0, 1))) { ResetPresetToCustom(); RefreshInterpolationState(); } }
    }
    /// <summary>Gets or sets the frame rate multiplier or custom-rate mode.</summary>
    public int InterpolationRateIndex
    {
        get;
        set { if (!IsBusy && value >= 0 && SetProperty(ref field, Math.Clamp(value, 0, 2))) { ResetPresetToCustom(); RefreshInterpolationState(); } }
    }
    /// <summary>Gets or sets the preferred interpolation accelerator.</summary>
    public int InterpolationBackendIndex { get; set { if (!IsBusy && value >= 0) SetProperty(ref field, Math.Clamp(value, 0, 3)); } }
    /// <summary>Gets or sets whether software inference is allowed after hardware failure.</summary>
    public bool InterpolationAllowCpuFallback { get; set { if (!IsBusy) SetProperty(ref field, value); } }
    /// <summary>Gets or sets whether scene changes suppress interpolated motion.</summary>
    public bool InterpolationSceneDetection { get; set { if (!IsBusy) SetProperty(ref field, value); } } = true;
    /// <summary>Gets whether frame generation is enabled.</summary>
    public bool IsInterpolationEnabled => InterpolationModeIndex != 0;
    /// <summary>Gets whether neural interpolation settings are visible.</summary>
    public bool IsAiInterpolation => InterpolationModeIndex == 1;
    /// <summary>Gets whether interpolation controls the non-GIF target rate.</summary>
    public bool ShowInterpolationRate => IsInterpolationEnabled && !IsGifMode;
    /// <summary>Gets whether a custom interpolation frame rate is selected.</summary>
    public bool ShowInterpolationFps => ShowInterpolationRate && InterpolationRateIndex == 2;
    /// <summary>Gets whether ordinary frame rate conversion settings are visible.</summary>
    public bool ShowOrdinaryFps => !IsInterpolationEnabled;
    /// <summary>Gets whether the selected interpolation model exists on disk.</summary>
    public bool IsInterpolationModelReady => _interpolationService?.IsModelAvailable((VideoInterpolationModel)InterpolationModelIndex) == true;
    /// <summary>Gets the localized install or verify action.</summary>
    public string InterpolationInstallLabel => LocalizationRegistry.Get(IsInterpolationModelReady ? "VideoTranscode.Sr_VerifyModel" : "VideoTranscode.Sr_InstallModel");
    /// <summary>Gets whether interpolation models or hardware providers are being installed.</summary>
    public bool IsInterpolationInstalling { get; private set { if (SetProperty(ref field, value)) RefreshInterpolationState(); } }
    /// <summary>Gets whether hardware provider installation is active.</summary>
    public bool IsInterpolationHardwareInstalling { get; private set => SetProperty(ref field, value); }
    /// <summary>Gets the normalized download progress.</summary>
    public double InterpolationDownloadProgress { get; private set => SetProperty(ref field, value); }
    /// <summary>Gets the model installation status.</summary>
    public string InterpolationDownloadText { get; private set => SetProperty(ref field, value); } = string.Empty;
    /// <summary>Gets the actual interpolation device and fallback indication.</summary>
    public string InterpolationDeviceText { get; private set => SetProperty(ref field, value); } = string.Empty;

    private void RefreshInterpolationState()
    {
        foreach (string property in new[] { nameof(IsInterpolationEnabled), nameof(IsAiInterpolation), nameof(ShowInterpolationRate),
                     nameof(ShowInterpolationFps), nameof(ShowOrdinaryFps), nameof(ShowInterpolationOrder), nameof(IsInterpolationModelReady), nameof(InterpolationInstallLabel) })
            OnPropertyChanged(property);
        InstallInterpolationModelCommand.NotifyCanExecuteChanged();
        InstallInterpolationAccelerationCommand.NotifyCanExecuteChanged();
        StartTranscodeCommand.NotifyCanExecuteChanged();
    }

    private bool CanInstallInterpolation() => !IsBusy && !IsInterpolationInstalling && _interpolationService != null;

    [RelayCommand(CanExecute = nameof(CanInstallInterpolation))]
    private Task InstallInterpolationModel() => InstallInterpolation(false);

    [RelayCommand(CanExecute = nameof(CanInstallInterpolation))]
    private Task InstallInterpolationAcceleration() => InstallInterpolation(true);

    private async Task InstallInterpolation(bool hardware)
    {
        if (_interpolationService == null) return;
        IsInterpolationInstalling = true;
        IsInterpolationHardwareInstalling = hardware;
        using var cancellation = _interpolationInstallCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            if (hardware) await _interpolationService.InstallAccelerationAsync(cancellation.Token);
            else await _interpolationService.EnsureModelAsync((VideoInterpolationModel)InterpolationModelIndex, cancellation.Token);
            InterpolationDownloadText = LocalizationRegistry.Get(hardware ? "VideoTranscode.Sr_HardwareReady" : "VideoTranscode.Sr_ModelReady");
        }
        catch (OperationCanceledException) { InterpolationDownloadText = LocalizationRegistry.Get("VideoTranscode.Sr_InstallCanceled"); }
        catch (Exception ex) { InterpolationDownloadText = ex.Message; if (!_isDisposed) _notificationService.ShowError(ex.Message); }
        finally { _interpolationInstallCts = null; if (!_isDisposed) { IsInterpolationHardwareInstalling = false; IsInterpolationInstalling = false; } }
    }

    [RelayCommand]
    private void CancelInterpolationInstallation() => _interpolationInstallCts?.Cancel();

    private void OnInterpolationDownload(ModelDownloadProgress progress) => Dispatcher.UIThread.Post(() =>
    {
        if (_isDisposed) return;
        InterpolationDownloadProgress = progress.TotalBytes is > 0 ? Math.Clamp(progress.BytesDownloaded / (double)progress.TotalBytes.Value, 0, 1) : 0;
        InterpolationDownloadText = progress.Stage == ModelDownloadStage.Done ? LocalizationRegistry.Get("VideoTranscode.Sr_ModelReady")
            : progress.Message ?? $"{progress.FileName} · {progress.BytesDownloaded / 1048576d:0.0} MB";
        RefreshInterpolationState();
    });

    private void OnInterpolationDevice(VideoSrDevice device) => Dispatcher.UIThread.Post(() =>
    {
        if (!_isDisposed) InterpolationDeviceText = device.Name + (device.FallbackReason == null ? string.Empty : " · " + LocalizationRegistry.Get("VideoTranscode.Sr_Fallback"));
    });
}
