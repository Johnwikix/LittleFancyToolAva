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

    private bool CanInstallSrAcceleration() => !IsBusy && !IsHardwareInstalling;

    [RelayCommand(CanExecute = nameof(CanInstallSrAcceleration))]
    private async Task InstallSrAcceleration()
    {
        IsHardwareInstalling = true;
        using var cancellation = _hardwareInstallCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            SrDeviceText = LocalizationRegistry.Get("VideoTranscode.Sr_InstallingHardware");
            await _superResolutionService.InstallAccelerationAsync(cancellation.Token);
            _superResolutionService.ReleaseSessions();
            SrDeviceText = LocalizationRegistry.Get("VideoTranscode.Sr_HardwareReady");
        }
        catch (OperationCanceledException) { SrDeviceText = LocalizationRegistry.Get("VideoTranscode.Sr_InstallCanceled"); }
        catch (Exception ex) { SrDeviceText = ex.Message; _notificationService.ShowError(ex.Message); }
        finally { _hardwareInstallCts = null; IsHardwareInstalling = false; }
    }

    [RelayCommand]
    private void CancelSrAccelerationInstall() => _hardwareInstallCts?.Cancel();
}
