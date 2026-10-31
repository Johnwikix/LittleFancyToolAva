#if WINDOWS && FANCYTOOL_MSIX
using Serilog;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace FancyToolAva.Services;

/// <summary>所有 MSIX 签名类型均查询真实 Store 许可；缺少包身份或查询失败不能放行。</summary>
internal sealed class WindowsStoreLicenseBackend : IStoreLicenseBackend
{
    private StoreContext? _context;
    private string? _storeId;
    private bool _disposed;
    private bool _inactiveDiagnosticsRead;

    public event Action? OfflineLicensesChanged;

    public void Initialize(nint ownerHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_context != null) return;
        // 强制确认包身份；开发签名、旁加载与复制出安装目录的 exe 均没有放行分支。
        Package package = Package.Current;
        Log.Information("Store 包身份 {Name}，PFN {FamilyName}，签名 {SignatureKind}，开发部署 {IsDevelopmentMode}",
            package.Id.Name, package.Id.FamilyName, package.SignatureKind, package.IsDevelopmentMode);
        if (!string.Equals(package.Id.FamilyName, StoreAppIdentity.PackageFamilyName, StringComparison.OrdinalIgnoreCase))
            throw new StoreIdentityMismatchException();
        if (ownerHandle == 0) throw new InvalidOperationException("Store 许可初始化需要主窗口 HWND。");
        var context = StoreContext.GetDefault();
        WinRT.Interop.InitializeWithWindow.Initialize(context, ownerHandle);
        context.OfflineLicensesChanged += OnOfflineLicensesChanged;
        _context = context;
    }

    public async Task<StoreLicenseSnapshot> GetLicenseAsync()
    {
        StoreAppLicense license = await (_context ?? throw new InvalidOperationException("Store 未初始化。"))
            .GetAppLicenseAsync();
        Log.Information("Store 许可原始结果：IsActive={IsActive}，IsTrial={IsTrial}，SkuStoreId={SkuStoreId}，Expiration={Expiration}",
            license.IsActive, license.IsTrial, license.SkuStoreId, license.ExpirationDate);
        if (!license.IsActive && !_inactiveDiagnosticsRead)
        {
            _inactiveDiagnosticsRead = true;
            try
            {
                var product = await ReadProductAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Log.Information("Store 商品关联：StoreId={StoreId}，IsInUserCollection={IsInUserCollection}，Error={Error}",
                    product.Product?.StoreId, product.Product?.IsInUserCollection, product.ExtendedError?.HResult);
            }
            catch (Exception ex) { Log.Warning(ex, "读取非活跃许可的商品诊断信息失败；不改变授权结果"); }
        }
        return new(license.IsActive, license.IsTrial, license.IsTrial ? license.ExpirationDate : null);
    }

    private async Task<StoreProductResult> ReadProductAsync() => await _context!.GetStoreProductForCurrentAppAsync();

    public async Task PurchaseAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            if (_context is { } context)
            {
                if (_storeId == null)
                {
                    var product = await context.GetStoreProductForCurrentAppAsync();
                    _storeId = product.Product?.StoreId;
                }
                if (_disposed) return;
                if (string.Equals(_storeId, StoreAppIdentity.ProductId, StringComparison.OrdinalIgnoreCase))
                {
                    var result = await context.RequestPurchaseAsync(_storeId);
                    Log.Information("应用购买返回 {Status}，错误 {Error}", result.Status, result.ExtendedError?.Message);
                    return;
                }
            }
        }
        catch (Exception ex) { Log.Warning(ex, "应用购买失败，打开本应用的商店页面"); }
        if (!_disposed && !await Windows.System.Launcher.LaunchUriAsync(new Uri($"ms-windows-store://pdp/?ProductId={StoreAppIdentity.ProductId}")))
            throw new InvalidOperationException(LocalizationRegistry.Get("License.StoreOpenFailed"));
    }

    private void OnOfflineLicensesChanged(StoreContext sender, object args) => OfflineLicensesChanged?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_context is { } context) context.OfflineLicensesChanged -= OnOfflineLicensesChanged;
        _context = null;
        OfflineLicensesChanged = null;
    }
}
#endif
