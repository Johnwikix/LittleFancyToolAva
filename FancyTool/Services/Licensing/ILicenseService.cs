namespace FancyToolAva.Services;

public enum AppLicenseState
{
    StoreUnavailable,
    FullLicense,
    TrialActive,
    LicenseInactive
}

[Flags]
public enum LicenseFeature
{
    None = 0,
    ImageSuperResolution = 1,
    VideoSuperResolution = 2,
    AiFrameInterpolation = 4,
    AllAi = ImageSuperResolution | VideoSuperResolution | AiFrameInterpolation
}

/// <summary>许可状态的唯一来源；非 MSIX 构建和非 Store 渠道始终放行。</summary>
public interface ILicenseService : IDisposable
{
    AppLicenseState State { get; }
    bool IsInitialized { get; }
    bool IsRestricted { get; }
    bool CanPurchase { get; }
    int? TrialRemainingDays { get; }
    DateTimeOffset? TrialExpiration { get; }
    event Action? StateChanged;
    bool CanUseFeature(LicenseFeature feature);
    bool IsFeatureRestricted(LicenseFeature feature);
    void ThrowIfRestricted(LicenseFeature feature);
    Task EnsureFeatureAsync(LicenseFeature feature, CancellationToken ct = default);
    Task InitializeAsync(nint ownerHandle);
    Task RefreshAsync();
    Task PurchaseAsync();
}

public sealed class LicenseRequiredException : InvalidOperationException
{
    public LicenseRequiredException() : base(LocalizationRegistry.Get("License.Required")) { }
}

internal readonly record struct StoreLicenseSnapshot(bool IsActive, bool IsTrial, DateTimeOffset? ExpirationDate);

/// <summary>可替换 Store 边界；生产后端只在 Windows MSIX 编译中存在。</summary>
internal interface IStoreLicenseBackend : IDisposable
{
    event Action? OfflineLicensesChanged;
    bool TryInitialize(nint ownerHandle);
    Task<StoreLicenseSnapshot> GetLicenseAsync();
    Task PurchaseAsync();
}

/// <summary>回归测试替换调度和时钟边界，生产状态始终由 Avalonia UI 线程维护。</summary>
internal interface ILicenseUiDispatcher
{
    bool CheckAccess();
    Task InvokeAsync(Func<Task> action);
    void Post(Action action);
    IDisposable StartTimer(TimeSpan interval, Action tick);
}
