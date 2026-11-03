#if FANCYTOOL_MSIX && WINDOWS
using Windows.ApplicationModel;
using Windows.Services.Store;
#endif

namespace FancyToolAva.Cli;

/// <summary>
/// MSIX 分发版 CLI 与 GUI 使用同一 Store 许可边界：未购买或试用过期时拒绝执行工具命令。
/// 仅帮助、版本与技能安装不受限制；非 MSIX 构建不启用该门禁。
/// </summary>
internal static class CliLicenseGate
{
#if FANCYTOOL_MSIX && WINDOWS
    private const string PackageFamilyName = "SennpaiStudio.---modbus_z79ft30j24epr";
#endif

    public static async Task<bool> EnsureAsync(bool zh)
    {
#if FANCYTOOL_MSIX && WINDOWS
        try
        {
            Package package = Package.Current;
            if (!string.Equals(package.Id.FamilyName, PackageFamilyName, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(zh ? "包身份与已发布商品不匹配，CLI 已锁定。" : "Package identity does not match the published product; CLI is locked.");
                return false;
            }
            StoreContext context = StoreContext.GetDefault();
            StoreAppLicense license = await context.GetAppLicenseAsync();
            if (!license.IsActive || (license.IsTrial && license.ExpirationDate <= DateTimeOffset.Now))
            {
                Console.Error.WriteLine(zh
                    ? "当前 Store 许可无效或试用已过期；请从 Microsoft Store 获取正式许可后重试。"
                    : "The Store license is inactive or the trial has expired. Obtain a full license from Microsoft Store and retry.");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine((zh ? "无法验证 Store 许可，CLI 已锁定: " : "Unable to verify the Store license; CLI is locked: ") + ex.Message);
            return false;
        }
#else
        await Task.CompletedTask;
        return true;
#endif
    }
}