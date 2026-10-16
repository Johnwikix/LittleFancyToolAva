using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Services;

namespace FancyToolAva.ViewModels;

public partial class VideoTranscodeViewModel
{
    private readonly IVideoInterpolationService? _interpolationService;
    private CancellationTokenSource? _interpolationInstallCts;
    private IReadOnlyList<HardwareInterpolationSupport> _hardwareInterpolationSupport = [];
    private int? _pendingHardwareModeIndex;

    /// <summary>Gets the localized interpolation methods.</summary>
    public IReadOnlyList<VideoInterpolationOption> InterpolationModes { get; } =
    [
        new("VideoTranscode.Interp_Off"), new("VideoTranscode.Interp_Ai"), new("VideoTranscode.Interp_Motion"),
        new("VideoTranscode.Interp_Amd", false, "VideoTranscode.Interp_Checking"),
        new("VideoTranscode.Interp_Nvidia", false, "VideoTranscode.Interp_Checking")
    ];
    /// <summary>Gets the localized RIFE model choices.</summary>
    public IReadOnlyList<LocalizedOption> InterpolationModels { get; } = [new("VideoTranscode.Interp_ModelLite"), new("VideoTranscode.Interp_ModelQuality")];
    /// <summary>Gets the output frame rate choices.</summary>
    public IReadOnlyList<VideoInterpolationOption> InterpolationRates { get; } = [new("VideoTranscode.Interp_Double"), new("VideoTranscode.Interp_Quadruple"), new("VideoTranscode.Interp_Sextuple")];
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
        set
        {
            if (IsBusy || IsInterpolationInstalling || value < 0 || value >= InterpolationModes.Count) return;
            if (!InterpolationModes[value].IsEnabled)
            {
                if (IsCheckingInterpolationHardware && value >= 3)
                {
                    _pendingHardwareModeIndex = value;
                    StartTranscodeCommand.NotifyCanExecuteChanged();
                }
                return;
            }
            bool hadPendingHardware = _pendingHardwareModeIndex.HasValue;
            _pendingHardwareModeIndex = null;
            if (SetProperty(ref field, value)) { ResetPresetToCustom(); RefreshInterpolationState(); }
            else if (hadPendingHardware) StartTranscodeCommand.NotifyCanExecuteChanged();
        }
    }
    /// <summary>Gets or sets the selected RIFE model.</summary>
    public int InterpolationModelIndex
    {
        get;
        set { if (!IsBusy && !IsInterpolationInstalling && value >= 0 && SetProperty(ref field, Math.Clamp(value, 0, 1))) { ResetPresetToCustom(); RefreshInterpolationState(); } }
    }
    /// <summary>Gets or sets the twofold, fourfold, or sixfold frame rate multiplier index.</summary>
    public int InterpolationRateIndex
    {
        get;
        set { if (!IsBusy && value >= 0 && value < InterpolationRates.Count && InterpolationRates[value].IsEnabled && SetProperty(ref field, value)) { ResetPresetToCustom(); RefreshInterpolationState(); } }
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
    /// <summary>Gets whether a vendor GPU optical flow method is selected.</summary>
    public bool IsHardwareInterpolation => HardwareVideoInterpolation.IsHardware((VideoInterpolationMode)InterpolationModeIndex);
    /// <summary>Gets whether configurable scene-cut protection is implemented by the selected method.</summary>
    public bool ShowInterpolationSceneDetection => !IsHardwareInterpolation;
    /// <summary>Gets whether hardware optical flow availability is still being checked.</summary>
    public bool IsCheckingInterpolationHardware { get; private set => SetProperty(ref field, value); } = true;
    /// <summary>Gets the availability status for AMD and NVIDIA interpolation.</summary>
    public string HardwareInterpolationStatus => string.Join(Environment.NewLine, InterpolationModes.Skip(3).Select(option => option.Label + " · " + option.Description));
    /// <summary>Gets whether the interpolation multiplier control is visible.</summary>
    public bool ShowInterpolationRate => IsInterpolationEnabled;
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
        var support = _hardwareInterpolationSupport.FirstOrDefault(value => (int)value.Mode == InterpolationModeIndex);
        for (int i = 0; i < InterpolationRates.Count; i++)
            InterpolationRates[i].SetAvailability(!IsHardwareInterpolation || support?.Multipliers.Contains((i + 1) * 2) == true,
                IsHardwareInterpolation && support?.Multipliers.Contains((i + 1) * 2) != true
                    ? InterpolationModeIndex == (int)VideoInterpolationMode.AmdOpticalFlow && i == 2 ? "VideoTranscode.Interp_AmdRates" : "VideoTranscode.Interp_UnsupportedMultiplier"
                    : null);
        if (!InterpolationRates[InterpolationRateIndex].IsEnabled)
            InterpolationRateIndex = Enumerable.Range(0, InterpolationRates.Count).First(index => InterpolationRates[index].IsEnabled);
        foreach (string property in new[] { nameof(IsInterpolationEnabled), nameof(IsAiInterpolation), nameof(IsHardwareInterpolation), nameof(ShowInterpolationSceneDetection), nameof(HardwareInterpolationStatus), nameof(ShowInterpolationRate),
                     nameof(ShowOrdinaryFps), nameof(ShowInterpolationOrder), nameof(IsInterpolationModelReady), nameof(InterpolationInstallLabel) })
            OnPropertyChanged(property);
        InstallInterpolationModelCommand.NotifyCanExecuteChanged();
        InstallInterpolationAccelerationCommand.NotifyCanExecuteChanged();
        StartTranscodeCommand.NotifyCanExecuteChanged();
    }

    private async Task CheckHardwareInterpolationAsync()
    {
        try
        {
            var support = await HardwareVideoInterpolation.GetCapabilitiesAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (_isDisposed) return;
                _hardwareInterpolationSupport = support;
                foreach (var capability in support)
                    InterpolationModes[(int)capability.Mode].SetAvailability(capability.IsSupported, capability.ReasonKey, capability.Detail);
                IsCheckingInterpolationHardware = false;
                var requested = _pendingHardwareModeIndex;
                _pendingHardwareModeIndex = null;
                if (requested is { } index && InterpolationModes[index].IsEnabled) InterpolationModeIndex = index;
                RefreshInterpolationState();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_isDisposed) return;
                foreach (var option in InterpolationModes.Skip(3)) option.SetAvailability(false, "VideoTranscode.Interp_ProbeFailed", ex.Message);
                IsCheckingInterpolationHardware = false;
                _pendingHardwareModeIndex = null;
                RefreshInterpolationState();
            });
        }
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
