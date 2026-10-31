using Serilog;

namespace FancyToolAva.Services;

/// <summary>参考 music_player 的 Store 查询、串行补查和购买流程；MSIX 的任何未知/失败状态都拒绝使用。</summary>
public sealed class LicenseService : ILicenseService
{
    private sealed record Status(AppLicenseState State, DateTimeOffset? Expiration, long TrialStart, TimeSpan TrialDuration);

    private readonly IStoreLicenseBackend? _backend;
    private readonly ILicenseDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _queryTimeout;
    private readonly List<IDisposable> _timers = [];
    private volatile Status _status;
    private volatile bool _disposed;
    private bool _initialized;
    private bool _backendReady;
    private bool _monitoringReady;
    private bool _refreshPending;
    private nint _ownerHandle;
    private TaskCompletionSource? _refreshCompletion;
    private TaskCompletionSource? _purchaseCompletion;
    private Task<StoreLicenseSnapshot>? _timedOutQuery;

    public LicenseService() : this(CreateBackend(), TimeProvider.System, new AvaloniaLicenseDispatcher()) { }

    internal LicenseService(IStoreLicenseBackend? backend, TimeProvider clock, ILicenseDispatcher dispatcher,
        TimeSpan? queryTimeout = null)
    {
        _backend = backend;
        _clock = clock;
        _dispatcher = dispatcher;
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(20);
        _status = new(backend == null ? AppLicenseState.Unrestricted : AppLicenseState.Checking, null, 0, TimeSpan.Zero);
    }

    private static IStoreLicenseBackend? CreateBackend()
    {
#if FANCYTOOL_MSIX
#if WINDOWS
        return new WindowsStoreLicenseBackend();
#else
#error MSIX 构建必须以 Windows 为目标，禁止静默回退为不受限渠道。
#endif
#else
        return null;
#endif
    }

    public AppLicenseState State => _status.State;
    public DateTimeOffset? TrialExpiration => _status.Expiration;
    public bool CanPurchase => !_disposed && _backend != null && _initialized && _purchaseCompletion == null
        && State != AppLicenseState.FullLicense;
    public bool CanUseApplication
    {
        get
        {
            Status status = _status;
            return !_disposed && (status.State is AppLicenseState.Unrestricted or AppLicenseState.FullLicense
                || (status.State == AppLicenseState.TrialActive && !HasExpired(status)));
        }
    }
    public event Action? StateChanged;

    public async Task InitializeAsync(nint ownerHandle)
    {
        if (_disposed || _backend == null) return;
        await RunOnUiAsync(async () =>
        {
            if (_initialized || _disposed) return;
            _initialized = true;
            _ownerHandle = ownerHandle;
            _backend.OfflineLicensesChanged += OnOfflineLicensesChanged;
            try
            {
                _timers.Add(_dispatcher.StartTimer(TimeSpan.FromMinutes(30), QueueRefresh));
                _timers.Add(_dispatcher.StartTimer(TimeSpan.FromSeconds(1), CheckTrialClock));
                _monitoringReady = true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "无法启动许可监控，保持锁定");
                ApplyState(AppLicenseState.VerificationFailed);
                return;
            }
            await RefreshOnUiAsync();
        });
    }

    public Task RefreshAsync() => _disposed || _backend == null
        ? Task.CompletedTask : RunOnUiAsync(RefreshOnUiAsync);

    private async Task RunOnUiAsync(Func<Task> action)
    {
        try { await _dispatcher.InvokeAsync(action); }
        catch (Exception ex)
        {
            // 调度失败也不能保留已放行状态；UI 退出时不再启动工具。
            Log.Warning(ex, "许可调度失败，拒绝使用工具");
            _status = new(AppLicenseState.VerificationFailed, null, 0, TimeSpan.Zero);
        }
    }

    private void OnOfflineLicensesChanged() => QueueRefresh();
    private void QueueRefresh() => _ = RefreshAsync(); // RefreshAsync 内部观察异常。

    private Task RefreshOnUiAsync()
    {
        if (_disposed || !_initialized || !_monitoringReady) return Task.CompletedTask;
        _refreshPending = true;
        if (_refreshCompletion is { } running) return running.Task;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshCompletion = completion;
        _ = DrainRefreshAsync(completion);
        return completion.Task;
    }

    private async Task DrainRefreshAsync(TaskCompletionSource completion)
    {
        try
        {
            do
            {
                _refreshPending = false;
                try
                {
                    if (!_backendReady)
                    {
                        _backend!.Initialize(_ownerHandle);
                        _backendReady = true;
                    }
                    if (_timedOutQuery is { } previousQuery)
                    {
                        // 取消等待没有停止 WinRT 调用。旧调用完成前禁止叠加查询，完成后的旧结果也作废。
                        if (!previousQuery.IsCompleted) break;
                        _timedOutQuery = null;
                    }
                    // WaitAsync 超时后不再使用迟到结果；底层任务的异常由 WaitAsync 观察。
                    Task<StoreLicenseSnapshot> query = _backend!.GetLicenseAsync();
                    StoreLicenseSnapshot license;
                    try { license = await query.WaitAsync(_queryTimeout, _clock); }
                    catch (TimeoutException) { _timedOutQuery = query; throw; }
                    if (_disposed) break;
                    if (!license.IsActive) ApplyState(AppLicenseState.LicenseInactive);
                    else if (!license.IsTrial) ApplyState(AppLicenseState.FullLicense);
                    else if (license.Expiration is { } expiration && expiration > _clock.GetUtcNow())
                        ApplyState(AppLicenseState.TrialActive, expiration);
                    else ApplyState(AppLicenseState.LicenseInactive);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Store 许可验证失败，禁用全部工具");
                    if (!_disposed) ApplyState(ex is StoreIdentityMismatchException
                        ? AppLicenseState.PackageIdentityMismatch : AppLicenseState.VerificationFailed);
                }
            } while (_refreshPending && !_disposed);
        }
        finally
        {
            _refreshCompletion = null;
            completion.TrySetResult();
        }
    }

    public Task PurchaseAsync() => _disposed || _backend == null
        ? Task.CompletedTask : RunOnUiAsync(PurchaseOnUiAsync);

    private Task PurchaseOnUiAsync()
    {
        if (_purchaseCompletion is { } running) return running.Task;
        if (!CanPurchase) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _purchaseCompletion = completion;
        RaiseStateChanged();
        _ = CompletePurchaseAsync(completion);
        return completion.Task;
    }

    private async Task CompletePurchaseAsync(TaskCompletionSource completion)
    {
        try
        {
            try { await _backend!.PurchaseAsync(); }
            catch (Exception ex) { Log.Warning(ex, "购买未完成，仍需核验许可"); }
            // 购买成功、取消和失败均重新查询，绝不根据购买返回值直接解锁。
            await RefreshOnUiAsync();
        }
        finally
        {
            _purchaseCompletion = null;
            if (!_disposed) RaiseStateChanged();
            completion.TrySetResult();
        }
    }

    private bool HasExpired(Status status) => status.Expiration is not { } expiration
        || _clock.GetUtcNow() >= expiration
        || _clock.GetElapsedTime(status.TrialStart) >= status.TrialDuration;

    private void CheckTrialClock()
    {
        if (!_disposed && _status.State == AppLicenseState.TrialActive && HasExpired(_status))
            ApplyState(AppLicenseState.LicenseInactive);
    }

    private void ApplyState(AppLicenseState state, DateTimeOffset? expiration = null)
    {
        if (_disposed) return;
        Status previous = _status;
        // 同一试用的重复查询不能因系统时钟回拨而延长已确认的运行时额度。
        Status next = state == AppLicenseState.TrialActive && previous.State == state && previous.Expiration == expiration
            ? previous : new(state, expiration, _clock.GetTimestamp(), expiration is { } end ? end - _clock.GetUtcNow() : TimeSpan.Zero);
        if (next.State == AppLicenseState.TrialActive && HasExpired(next))
            next = new(AppLicenseState.LicenseInactive, null, 0, TimeSpan.Zero);
        _status = next;
        if (previous == next) return;
        Log.Information("应用许可 {Previous} → {State}", previous.State, next.State);
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        foreach (Action handler in Delegate.EnumerateInvocationList(StateChanged))
        {
            try { handler(); }
            catch (Exception ex) { Log.Warning(ex, "许可状态订阅者失败"); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StateChanged = null;
        foreach (IDisposable timer in _timers)
        {
            try { timer.Dispose(); }
            catch (Exception ex) { Log.Warning(ex, "停止许可计时器失败"); }
        }
        _timers.Clear();
        if (_backend == null) return;
        try { _backend.OfflineLicensesChanged -= OnOfflineLicensesChanged; }
        catch (Exception ex) { Log.Warning(ex, "解除许可事件失败"); }
        try { _backend.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "释放 Store 后端失败"); }
    }
}
