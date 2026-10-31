using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using FancyToolAva.Services;
using Lang.Avalonia;
using System.Globalization;

namespace FancyToolAva.ViewModels;

/// <summary>锁定页与购买弹窗共用命令；不保存或覆盖授权结果。</summary>
public partial class LicenseViewModel : ViewModelBase, IDisposable
{
    private readonly ILicenseService _service;
    private bool _disposed;

    public LicenseViewModel(ILicenseService service)
    {
        _service = service;
        service.StateChanged += OnStateChanged;
        I18nManager.Instance.CultureChanged += OnCultureChanged;
    }

    public bool IsLocked => !_service.CanUseApplication;
    public bool IsChecking => _service.State == AppLicenseState.Checking || IsBusy;
    public bool ShowPurchase => _service.State is AppLicenseState.TrialActive or AppLicenseState.LicenseInactive;
    public string TitleText => LocalizationStrings.Current[_service.State switch
    {
        AppLicenseState.Checking => "License.CheckingTitle",
        AppLicenseState.PackageIdentityMismatch => "License.PackageMismatchTitle",
        AppLicenseState.VerificationFailed => "License.VerificationTitle",
        _ => "License.Title"
    }];
    public string StatusText => LocalizationStrings.Current[_service.State switch
    {
        AppLicenseState.Checking => "License.Checking",
        AppLicenseState.VerificationFailed => "License.VerificationFailed",
        AppLicenseState.PackageIdentityMismatch => "License.PackageMismatch",
        AppLicenseState.FullLicense => "License.FullLicense",
        AppLicenseState.Unrestricted => "License.Unrestricted",
        AppLicenseState.TrialActive => "License.TrialActive",
        _ => "License.Required"
    }];
    public bool ShowTrialExpiration => _service.State == AppLicenseState.TrialActive && _service.TrialExpiration != null;
    public string TrialExpirationText => ShowTrialExpiration
        ? LocalizationRegistry.Get("License.TrialUntil", _service.TrialExpiration!.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
        : string.Empty;

    public bool IsBusy
    {
        get;
        private set
        {
            if (!SetProperty(ref field, value)) return;
            OnPropertyChanged(nameof(IsChecking));
            PurchaseCommand.NotifyCanExecuteChanged();
            RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanPurchase() => !_disposed && !IsBusy && _service.CanPurchase;
    private bool CanRefresh() => !_disposed && !IsBusy && _service.State != AppLicenseState.Checking;

    [RelayCommand(CanExecute = nameof(CanPurchase))]
    private Task PurchaseAsync() => RunAsync(purchase: true);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RunAsync(purchase: false);

    private async Task RunAsync(bool purchase)
    {
        if (purchase ? !CanPurchase() : !CanRefresh()) return;
        IsBusy = true;
        try
        {
            if (purchase) await _service.PurchaseAsync();
            else await _service.RefreshAsync();
        }
        finally { if (!_disposed) IsBusy = false; }
    }

    private void OnStateChanged()
    {
        if (!_disposed) NotifyAllTextChanged();
    }

    private void OnCultureChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        LocalizationStrings.Reload();
        NotifyAllTextChanged();
    });

    private void NotifyAllTextChanged()
    {
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(ShowPurchase));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowTrialExpiration));
        OnPropertyChanged(nameof(TrialExpirationText));
        PurchaseCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.StateChanged -= OnStateChanged;
        I18nManager.Instance.CultureChanged -= OnCultureChanged;
    }
}
