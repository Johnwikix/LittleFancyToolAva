namespace FancyToolAva.Services;

public enum AppLicenseState
{
    Unrestricted,
    Checking,
    FullLicense,
    TrialActive,
    LicenseInactive,
    VerificationFailed,
    PackageIdentityMismatch
}

/// <summary>唯一授权状态源。只有非 MSIX 构建、有效正式许可或有效试用可以启动工具。</summary>
public interface ILicenseService : IDisposable
{
    AppLicenseState State { get; }
    bool CanUseApplication { get; }
    bool CanPurchase { get; }
    DateTimeOffset? TrialExpiration { get; }
    event Action? StateChanged;
    Task InitializeAsync(nint ownerHandle);
    Task RefreshAsync();
    Task PurchaseAsync();
}

internal readonly record struct StoreLicenseSnapshot(bool IsActive, bool IsTrial, DateTimeOffset? Expiration);

internal interface IStoreLicenseBackend : IDisposable
{
    event Action? OfflineLicensesChanged;
    void Initialize(nint ownerHandle);
    Task<StoreLicenseSnapshot> GetLicenseAsync();
    Task PurchaseAsync();
}

internal interface ILicenseDispatcher
{
    Task InvokeAsync(Func<Task> action);
    IDisposable StartTimer(TimeSpan interval, Action tick);
}
