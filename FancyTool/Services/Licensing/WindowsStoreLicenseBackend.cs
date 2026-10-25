#if WINDOWS && FANCYTOOL_MSIX
using Serilog;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace FancyToolAva.Services;

/// <summary>仅 Store 签名 MSIX 访问许可 API；不持久化和伪造本地许可。</summary>
internal sealed class WindowsStoreLicenseBackend : IStoreLicenseBackend
{
    private StoreContext? _context;
    private string? _storeId;
    private string? _packageFamilyName;
    private bool _disposed;

    public event Action? OfflineLicensesChanged;

    public bool TryInitialize(nint ownerHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Package package = Package.Current;
        if (package.SignatureKind != PackageSignatureKind.Store)
        {
            Log.Information("非 Store 签名包，AI 许可门控不生效，签名类型 {SignatureKind}", package.SignatureKind);
            return false;
        }
        if (ownerHandle == 0) throw new InvalidOperationException("初始化 Store 许可需要有效主窗口句柄。");
        _packageFamilyName = package.Id.FamilyName;
        var context = StoreContext.GetDefault();
        WinRT.Interop.InitializeWithWindow.Initialize(context, ownerHandle);
        context.OfflineLicensesChanged += OnOfflineLicensesChanged;
        _context = context;
        return true;
    }

    public async Task<StoreLicenseSnapshot> GetLicenseAsync()
    {
        StoreAppLicense license = await GetContext().GetAppLicenseAsync();
        return new StoreLicenseSnapshot(license.IsActive, license.IsTrial,
            license.IsTrial ? license.ExpirationDate : null);
    }

    public async Task PurchaseAsync()
    {
        StoreContext context = GetContext();
        try
        {
            if (_storeId is null)
            {
                StoreProductResult product = await context.GetStoreProductForCurrentAppAsync();
                _storeId = product.Product?.StoreId;
                if (product.ExtendedError is { } error)
                    Log.Warning(error, "查询当前应用 StoreId 失败，尝试打开应用商店页面");
            }
            if (_disposed) return;
            if (string.IsNullOrWhiteSpace(_storeId))
            {
                await OpenStorePageAsync();
                return;
            }
            StorePurchaseResult result = await context.RequestPurchaseAsync(_storeId);
            Log.Information("Store 购买请求返回 {Status}，错误 {Error}", result.Status, result.ExtendedError?.Message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Store 购买请求失败，尝试打开当前应用商店页面");
            await OpenStorePageAsync();
        }
    }

    private async Task OpenStorePageAsync()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_packageFamilyName)) return;
        try
        {
            var uri = new Uri($"ms-windows-store://pdp/?PFN={Uri.EscapeDataString(_packageFamilyName)}");
            bool opened = await Windows.System.Launcher.LaunchUriAsync(uri);
            if (!opened) Log.Warning("系统未能打开当前应用的 Microsoft Store 页面");
        }
        catch (Exception ex) { Log.Warning(ex, "打开当前应用 Microsoft Store 页面失败"); }
    }

    private StoreContext GetContext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _context ?? throw new InvalidOperationException("Store 许可后端尚未初始化。");
    }

    private void OnOfflineLicensesChanged(StoreContext sender, object args)
    {
        if (!_disposed) OfflineLicensesChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_context is { } context) context.OfflineLicensesChanged -= OnOfflineLicensesChanged;
        }
        finally
        {
            _context = null;
            OfflineLicensesChanged = null;
        }
    }
}
#endif
