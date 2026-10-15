using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.AudioEnhancement;
using FancyToolAva.Models;
using FancyToolAva.Services;

namespace FancyToolAva.ViewModels;

public partial class AudioConvertViewModel
{
    private AudioSrModelService? _audioSrModels;
    private CancellationTokenSource? _audioSrInstallCts;
    private readonly Dictionary<string, string> _audioSrDevices = new();
    /// <summary>Gets the installed signal-processing backend choices.</summary>
    public IReadOnlyList<string> UpsamplingBackendLabels { get; } = ["DSP", "AudioSR"];
    /// <summary>Gets or sets the UI selector backed by the persisted stable backend identifier.</summary>
    public int UpsamplingBackendIndex
    {
        get => IsAudioSrSelected ? 1 : 0;
        set { if (value is 0 or 1) UpsamplingBackendId = value == 1 ? AudioSrUpsamplingBackend.BackendId : AudioUpsamplingBackendRegistry.DefaultBackendId; }
    }
    /// <summary>Gets whether the selected backend uses the AudioSR model.</summary>
    public bool IsAudioSrSelected => UpsamplingBackendId == AudioSrUpsamplingBackend.BackendId;
    /// <summary>Gets whether AudioSR applies to the current lossless-output conversion.</summary>
    public bool IsAudioSrActive => IsUpsamplingActive && IsAudioSrSelected;
    /// <summary>Gets the selected backend's input format limits.</summary>
    public string UpsamplingInputHint => LocalizationRegistry.Get(IsAudioSrSelected ? "AudioTranscode.Sr_InputHint" : "AudioTranscode.Dsp_InputHint");
    /// <summary>Gets the shared image/video hardware preference labels.</summary>
    public IReadOnlyList<LocalizedOption> AudioSrAccelerationLabels { get; } = [new("VideoTranscode.Sr_BackendAuto"),
        new("VideoTranscode.Sr_BackendNpu"), new("VideoTranscode.Sr_BackendGpu"), new("VideoTranscode.Sr_BackendCpu")];
    /// <summary>Gets or sets the shared Windows ML hardware preference.</summary>
    public int AudioSrAccelerationIndex { get => field; set { if (value is >= 0 and <= 3) SetProperty(ref field, value); } }
    /// <summary>Gets or sets whether unsupported hardware may use CPU inference.</summary>
    public bool AudioSrAllowCpuFallback { get => field; set => SetProperty(ref field, value); } = true;
    /// <summary>Gets the diffusion iteration choices.</summary>
    public IReadOnlyList<int> AudioSrStepChoices { get; } = [20, 50, 100];
    /// <summary>Gets or sets the diffusion step count; older settings retain the default fifty steps.</summary>
    public int AudioSrSteps { get => field; set => SetProperty(ref field, value is 20 or 50 or 100 ? value : 50); } = 50;
    /// <summary>Gets whether model files or hardware providers are being installed.</summary>
    public bool IsAudioSrInstalling
    {
        get => field;
        private set { if (SetProperty(ref field, value)) RefreshAudioSrState(); }
    }
    /// <summary>Gets the normalized current-file download progress.</summary>
    public double AudioSrDownloadProgress { get => field; private set => SetProperty(ref field, value); }
    /// <summary>Gets the current model download, graph loading or diffusion status.</summary>
    public string AudioSrStatusText { get => field; private set => SetProperty(ref field, value); } = "";
    /// <summary>Gets the actual provider selected for all four neural graphs.</summary>
    public string AudioSrDeviceText { get => field; private set => SetProperty(ref field, value); } = "";
    /// <summary>Gets the localized install/verify button label.</summary>
    public string AudioSrInstallLabel => LocalizationRegistry.Get(_audioSrModels?.IsModelAvailable == true
        ? "VideoTranscode.Sr_VerifyModel" : "VideoTranscode.Sr_InstallModel");

    private void InitializeAudioSr(AudioSrModelService? models)
    {
        _audioSrModels = models;
        if (models == null) return;
        models.DownloadProgressChanged += OnAudioSrDownload;
        models.DeviceChanged += OnAudioSrDevice;
        models.InferenceProgressChanged += OnAudioSrInference;
    }

    private void RefreshAudioSrState()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(RefreshAudioSrState); return; }
        if (_isDisposed) return;
        OnPropertyChanged(nameof(AudioSrInstallLabel));
        InstallAudioSrModelCommand.NotifyCanExecuteChanged();
        InstallAudioSrAccelerationCommand.NotifyCanExecuteChanged();
        StartConvertCommand.NotifyCanExecuteChanged();
    }

    private bool CanInstallAudioSr() => !IsBusy && !IsAudioSrInstalling && _audioSrModels != null;

    [RelayCommand(CanExecute = nameof(CanInstallAudioSr))]
    private async Task InstallAudioSrModel() => await InstallAudioSr(false);

    [RelayCommand(CanExecute = nameof(CanInstallAudioSr))]
    private async Task InstallAudioSrAcceleration() => await InstallAudioSr(true);

    private async Task InstallAudioSr(bool hardware)
    {
        if (!CanInstallAudioSr()) return;
        IsAudioSrInstalling = true;
        using var cancellation = _audioSrInstallCts = new CancellationTokenSource();
        try
        {
            if (hardware) await _audioSrModels.InstallAccelerationAsync(cancellation.Token);
            else await _audioSrModels.EnsureModelAsync(cancellation.Token);
            if (!_isDisposed) AudioSrStatusText = LocalizationRegistry.Get(hardware ? "VideoTranscode.Sr_HardwareReady" : "VideoTranscode.Sr_ModelReady");
        }
        catch (OperationCanceledException) { if (!_isDisposed) AudioSrStatusText = LocalizationRegistry.Get("VideoTranscode.Sr_InstallCanceled"); }
        catch (Exception error) { if (!_isDisposed) { AudioSrStatusText = error.Message; _notificationService.ShowError(error.Message); } }
        finally { _audioSrInstallCts = null; if (!_isDisposed) IsAudioSrInstalling = false; }
    }

    [RelayCommand]
    private void CancelAudioSrInstallation() => _audioSrInstallCts?.Cancel();

    private void OnAudioSrDownload(ModelDownloadProgress item) => Dispatcher.UIThread.Post(() =>
    {
        if (_isDisposed) return;
        if (item.FileName == "vae_feature_extract") { _audioSrDevices.Clear(); AudioSrDeviceText = ""; }
        AudioSrDownloadProgress = item.TotalBytes is > 0 ? Math.Clamp(item.BytesDownloaded / (double)item.TotalBytes, 0, 1) : 0;
        AudioSrStatusText = item.Message ?? $"{item.FileName} · {item.BytesDownloaded / 1048576.0:0.0} MB";
        RefreshAudioSrState();
    });

    private void OnAudioSrDevice(VideoSrDevice device) => Dispatcher.UIThread.Post(() =>
    {
        if (_isDisposed) return;
        string graph = device.Name.Split('·')[0].Trim();
        _audioSrDevices[graph] = device.Name + (device.FallbackReason != null ? " · " + LocalizationRegistry.Get("VideoTranscode.Sr_Fallback") : "");
        AudioSrDeviceText = string.Join(Environment.NewLine, _audioSrDevices.Values);
    });

    private void OnAudioSrInference(int done, int total) => Dispatcher.UIThread.Post(() =>
    {
        if (!_isDisposed) AudioSrStatusText = LocalizationRegistry.Get("AudioTranscode.Sr_Processing", done, total);
    });

    private void DisposeAudioSr()
    {
        _audioSrInstallCts?.Cancel();
        if (_audioSrModels == null) return;
        _audioSrModels.DownloadProgressChanged -= OnAudioSrDownload;
        _audioSrModels.DeviceChanged -= OnAudioSrDevice;
        _audioSrModels.InferenceProgressChanged -= OnAudioSrInference;
        _audioSrModels = null;
    }
}
