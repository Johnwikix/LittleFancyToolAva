using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Services;

namespace FancyToolAva.ViewModels;

public partial class ImageConvertViewModel
{
    private CancellationTokenSource? _hardwareInstallCts;

    /// <summary>Gets whether Windows ML hardware components are being installed.</summary>
    public bool IsHardwareInstalling
    {
        get => field;
        private set
        {
            if (SetProperty(ref field, value))
            {
                InstallSrAccelerationCommand.NotifyCanExecuteChanged();
                StartConvertCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool CanInstallSrAcceleration() => !_isDisposed && !IsBusy && !IsHardwareInstalling
        && _licenseService.CanUseFeature(LicenseFeature.ImageSuperResolution);

    [RelayCommand(CanExecute = nameof(CanInstallSrAcceleration))]
    private async Task InstallSrAcceleration()
    {
        if (_isDisposed || IsBusy || IsHardwareInstalling || !await EnsureAiAccessAsync()) return;
        if (_isDisposed || IsBusy || IsHardwareInstalling) return;
        IsHardwareInstalling = true;
        using var cancellation = _hardwareInstallCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            SrDeviceText = LocalizationRegistry.Get("VideoTranscode.Sr_InstallingHardware");
            await _superResolutionService.InstallAccelerationAsync(cancellation.Token);
            // ReleaseSessions 需持推理锁并同步 Dispose 原生 DML 会话，不能在 UI 线程执行。
            await Task.Run(() => _superResolutionService.ReleaseSessions());
            SrDeviceText = LocalizationRegistry.Get("VideoTranscode.Sr_HardwareReady");
        }
        catch (OperationCanceledException) { SrDeviceText = LocalizationRegistry.Get("VideoTranscode.Sr_InstallCanceled"); }
        catch (Exception ex) { if (!_isDisposed) { SrDeviceText = ex.Message; _notificationService.ShowError(ex.Message); } }
        finally { _hardwareInstallCts = null; IsHardwareInstalling = false; }
    }

    [RelayCommand]
    private void CancelSrAccelerationInstall() => _hardwareInstallCts?.Cancel();
}
