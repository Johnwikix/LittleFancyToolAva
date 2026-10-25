using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Services;
using Lang.Avalonia;

namespace FancyToolAva.ViewModels;

/// <summary>三个页面共享的商店授权状态与购买操作；由应用单例持有。</summary>
public partial class LicenseViewModel : ViewModelBase, IDisposable
{
    private readonly ILicenseService _service;
    private bool _isDisposed;

    internal ILicenseService Service => _service;

    public LicenseViewModel(ILicenseService service)
    {
        _service = service;
        _service.StateChanged += OnLicenseStateChanged;
        I18nManager.Instance.CultureChanged += OnCultureChanged;
    }

    /// <summary>普通 Windows 与 Linux 构建不显示商店授权区。</summary>
    public bool IsNoticeVisible => !_service.IsInitialized || _service.State != AppLicenseState.StoreUnavailable;
    private static string GetText(string key) => LocalizationStrings.Current.TryGetValue(key, out string? value)
        ? value : LocalizationRegistry.Get(key);
    public string TitleText => GetText("License.Title");
    public string ScopeText => GetText("License.Scope");
    public string PurchaseLabel => GetText("License.Purchase");
    public string RefreshLabel => GetText("License.Refresh");
    public bool ShowPurchase => _service.IsInitialized
        && _service.State is AppLicenseState.TrialActive or AppLicenseState.LicenseInactive;
    public bool IsRestricted => _service.IsRestricted;
    public bool IsChecking => !_service.IsInitialized || IsBusy;

    public string StatusText
    {
        get
        {
            if (!_service.IsInitialized) return GetText("License.Checking");
            return _service.State switch
            {
                AppLicenseState.FullLicense => GetText("License.FullLicense"),
                AppLicenseState.TrialActive when _service.TrialRemainingDays is { } days && days > 0
                    => LocalizationRegistry.Get("License.TrialDays", days),
                AppLicenseState.TrialActive when _service.TrialRemainingDays is not null
                    => GetText("License.TrialLastDay"),
                AppLicenseState.TrialActive => GetText("License.TrialActive"),
                AppLicenseState.LicenseInactive => GetText("License.Required"),
                _ => string.Empty
            };
        }
    }

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

    public string ErrorText
    {
        get;
        private set
        {
            if (SetProperty(ref field, value)) OnPropertyChanged(nameof(HasError));
        }
    } = string.Empty;
    public bool HasError => ErrorText.Length != 0;

    private bool CanPurchase() => !_isDisposed && !IsBusy && _service.IsInitialized && _service.CanPurchase;
    private bool CanRefresh() => !_isDisposed && !IsBusy && _service.IsInitialized;

    [RelayCommand(CanExecute = nameof(CanPurchase))]
    private Task Purchase() => RunStoreActionAsync(purchase: true);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task Refresh() => RunStoreActionAsync(purchase: false);

    private async Task RunStoreActionAsync(bool purchase)
    {
        // 不同页面绑定同一组命令；购买与刷新也共享忙状态，避免相互抢占商店界面。
        if (_isDisposed || IsBusy || !_service.IsInitialized || (purchase && !_service.CanPurchase)) return;
        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            if (purchase) await _service.PurchaseAsync();
            else await _service.RefreshAsync();
        }
        catch (Exception ex)
        {
            if (!_isDisposed) ErrorText = ex.Message;
        }
        finally
        {
            if (!_isDisposed)
            {
                IsBusy = false;
                NotifyAllTextChanged();
            }
        }
    }

    private void OnLicenseStateChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_isDisposed) return;
        ErrorText = string.Empty;
        NotifyAllTextChanged();
    });

    private void OnCultureChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_isDisposed) return;
        // 语言也可由启动恢复和外部入口切换，不能依赖设置页先刷新字符串表。
        LocalizationStrings.Reload();
        NotifyAllTextChanged();
    });

    private void NotifyAllTextChanged()
    {
        OnPropertyChanged(nameof(IsNoticeVisible));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(ScopeText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PurchaseLabel));
        OnPropertyChanged(nameof(RefreshLabel));
        OnPropertyChanged(nameof(ShowPurchase));
        OnPropertyChanged(nameof(IsRestricted));
        OnPropertyChanged(nameof(IsChecking));
        PurchaseCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _service.StateChanged -= OnLicenseStateChanged;
        I18nManager.Instance.CultureChanged -= OnCultureChanged;
        PurchaseCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }
}
