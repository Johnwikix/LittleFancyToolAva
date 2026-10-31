namespace FancyToolAva.Services;

/// <summary>已发布商品的实际身份；来源为 Microsoft Store 商品目录，不按本地项目名推断。</summary>
internal static class StoreAppIdentity
{
    public const string ProductId = "9P543GHQQKVK";
    public const string PackageName = "SennpaiStudio.---modbus";
    public const string PackageFamilyName = "SennpaiStudio.---modbus_z79ft30j24epr";
}

internal sealed class StoreIdentityMismatchException : InvalidOperationException
{
    public StoreIdentityMismatchException() : base(LocalizationRegistry.Get("License.PackageMismatch")) { }
}
