using Serilog;

namespace FancyToolAva.Services;

/// <summary>
/// Store 确认许可非活跃时限制 AI；首次查询失败放行，后续失败保留上次结果。
/// 初始化和后台刷新均由 UI 线程合并，实际推理入口还需调用 EnsureFeatureAsync。
/// </summary>
public sealed class LicenseService : ILicenseService
{
    private sealed record Status(AppLicenseState State, DateTimeOffset? Expiration, bool Initialized);

    private readonly IStoreLicenseBackend? _backend;
    private readonly TimeProvider _timeProvider;
    private readonly ILicenseUiDispatcher _dispatcher;
    private readonly TaskCompletionSource _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<Task> _refreshOnUi;
    private readonly Func<Task> _purchaseOnUi;
    private volatile Status _status;
    private volatile bool _disposed;
    private int _disposeSignaled;
    private bool _initializationStarted;
    private bool _backendReady;
    private bool _refreshPending;
    private int? _lastNotifiedDays;
    private TaskCompletionSource? _refreshCompletion;
    private TaskCompletionSource? _purchaseCompletion;
    private IDisposable? _refreshTimer;
    private IDisposable? _trialTimer;

    public static ILicenseService Unrestricted { get; } = new UnrestrictedLicenseService();

    public LicenseService() : this(CreateBackend(), TimeProvider.System, AvaloniaLicenseDispatcher.Instance) { }

    internal LicenseService(IStoreLicenseBackend? backend, TimeProvider timeProvider, ILicenseUiDispatcher dispatcher)
    {
        _backend = backend;
        _timeProvider = timeProvider;
        _dispatcher = dispatcher;
        _refreshOnUi = RefreshOnUiAsync;
        _purchaseOnUi = PurchaseOnUiAsync;
        _status = new Status(AppLicenseState.StoreUnavailable, null, backend is null);
        if (backend is null) _initialization.TrySetResult();
    }

    private static IStoreLicenseBackend? CreateBackend()
    {
#if WINDOWS && FANCYTOOL_MSIX
        return OperatingSystem.IsWindows() ? new WindowsStoreLicenseBackend() : null;
#else
        return null;
#endif
    }

    public AppLicenseState State => _status.State;
    public bool IsInitialized => _status.Initialized;
    public bool IsRestricted => State == AppLicenseState.LicenseInactive;
    public bool CanPurchase => !_disposed && IsInitialized && _purchaseCompletion is null
        && State is AppLicenseState.TrialActive or AppLicenseState.LicenseInactive;
    public DateTimeOffset? TrialExpiration => _status.Expiration;
    public int? TrialRemainingDays => GetRemainingDays(_status);
    public event Action? StateChanged;

    public bool IsFeatureRestricted(LicenseFeature feature) => IsRestricted && (feature & LicenseFeature.AllAi) != 0;

    public bool CanUseFeature(LicenseFeature feature) => feature == LicenseFeature.None
        || (!_disposed && IsInitialized && !IsFeatureRestricted(feature));

    public void ThrowIfRestricted(LicenseFeature feature)
    {
        if (feature == LicenseFeature.None) return;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsInitialized) throw new InvalidOperationException(LocalizationRegistry.Get("License.Checking"));
        if (IsFeatureRestricted(feature)) throw new LicenseRequiredException();
    }

    public async Task EnsureFeatureAsync(LicenseFeature feature, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (feature == LicenseFeature.None) return;
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initialization.Task.WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        ThrowIfRestricted(feature);
    }

    public async Task InitializeAsync(nint ownerHandle)
    {
        if (_disposed || _backend is null) return;
        try { await _dispatcher.InvokeAsync(() => InitializeOnUiAsync(ownerHandle)); }
        catch (Exception ex)
        {
            // 调度器退出时也解除初始化等待；此时不能再向关闭的 UI 发布通知。
            Log.Warning(ex, "许可初始化调度失败，解除初始化等待");
            _initialization.TrySetResult();
        }
    }

    private Task InitializeOnUiAsync(nint ownerHandle)
    {
        if (_disposed || _initializationStarted || _backend is null) return _initialization.Task;
        _initializationStarted = true;
        try
        {
            _backendReady = _backend.TryInitialize(ownerHandle);
            if (_backendReady)
            {
                _backend.OfflineLicensesChanged += OnOfflineLicensesChanged;
                try { _refreshTimer = _dispatcher.StartTimer(TimeSpan.FromMinutes(30), QueueRefresh); }
                catch (Exception ex) { Log.Warning(ex, "许可定时刷新启动失败，继续查询并保留 Store 事件刷新"); }
                return FinishInitializationAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Store 许可初始化失败，首次查询按未受限处理");
        }
        CompleteInitialization();
        return _initialization.Task;
    }

    private async Task FinishInitializationAsync()
    {
        try { await RefreshOnUiAsync(); }
        finally { CompleteInitialization(); }
    }

    private void CompleteInitialization()
    {
        if (!_disposed && !IsInitialized)
        {
            _status = _status with { Initialized = true };
            RaiseStateChanged();
        }
        _initialization.TrySetResult();
    }

    public async Task RefreshAsync()
    {
        if (_disposed || _backend is null) return;
        try { await _dispatcher.InvokeAsync(_refreshOnUi); }
        catch (Exception ex) { Log.Warning(ex, "许可刷新调度失败，保留当前状态"); }
    }

    private void OnOfflineLicensesChanged() => QueueRefresh();

    private void QueueRefresh()
    {
        // RefreshAsync 负责观察所有异常，后台 Store 事件也统一回到 UI 线程。
        _ = RefreshAsync();
    }

    private Task RefreshOnUiAsync()
    {
        if (_disposed || !_backendReady) return Task.CompletedTask;
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
                    StoreLicenseSnapshot license = await _backend!.GetLicenseAsync();
                    if (_disposed) break;
                    if (!license.IsActive) ApplyState(AppLicenseState.LicenseInactive, null);
                    else if (license.IsTrial) ApplyState(AppLicenseState.TrialActive, license.ExpirationDate);
                    else ApplyState(AppLicenseState.FullLicense, null);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Store 许可查询失败，保留当前状态 {State}", State);
                }
                // 在途查询期间的刷新、购买及 Store 事件最多补查一次；等待方等到补查完成。
            } while (_refreshPending && !_disposed);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "许可状态发布失败，保留最后确认结果");
        }
        finally
        {
            _refreshCompletion = null;
            completion.TrySetResult();
        }
    }

    public async Task PurchaseAsync()
    {
        if (_disposed || _backend is null) return;
        try { await _dispatcher.InvokeAsync(_purchaseOnUi); }
        catch (Exception ex) { Log.Warning(ex, "购买请求调度失败"); }
    }

    private Task PurchaseOnUiAsync()
    {
        if (_purchaseCompletion is { } running) return running.Task;
        if (_disposed || !_backendReady || !CanPurchase) return Task.CompletedTask;
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
            catch (Exception ex) { Log.Warning(ex, "Store 购买请求失败，重新核对当前许可"); }
            // 成功、取消或失败都重新查询；购买响应本身不能解锁功能。
            await RefreshOnUiAsync();
        }
        catch (Exception ex) { Log.Warning(ex, "购买后许可刷新失败，保留当前状态"); }
        finally
        {
            _purchaseCompletion = null;
            if (!_disposed) RaiseStateChanged();
            completion.TrySetResult();
        }
    }

    private int? GetRemainingDays(Status status) => status.State == AppLicenseState.TrialActive
        && status.Expiration is { } expiration
        ? (int)Math.Clamp(Math.Ceiling((expiration - _timeProvider.GetUtcNow()).TotalDays), 0, int.MaxValue)
        : null;

    private void ApplyState(AppLicenseState state, DateTimeOffset? expiration)
    {
        Status previous = _status;
        var next = new Status(state, expiration, previous.Initialized);
        int? days = GetRemainingDays(next);
        bool changed = previous.State != state || previous.Expiration != expiration || _lastNotifiedDays != days;
        _status = next;
        _lastNotifiedDays = days;
        if (state == AppLicenseState.TrialActive && _trialTimer is null)
        {
            try { _trialTimer = _dispatcher.StartTimer(TimeSpan.FromMinutes(1), OnTrialClockTick); }
            catch (Exception ex) { Log.Warning(ex, "试用倒计时启动失败，保留已确认许可"); }
        }
        else if (state != AppLicenseState.TrialActive && _trialTimer is { } timer)
        {
            _trialTimer = null;
            try { timer.Dispose(); }
            catch (Exception ex) { Log.Warning(ex, "试用倒计时停止失败，继续发布许可状态"); }
        }
        if (!changed) return;
        Log.Information("许可状态：{Previous} → {State}，试用到期时间 {Expiration}", previous.State, state, expiration);
        RaiseStateChanged();
    }

    private void OnTrialClockTick()
    {
        if (_disposed || State != AppLicenseState.TrialActive) return;
        int? days = TrialRemainingDays;
        if (_lastNotifiedDays == days) return;
        _lastNotifiedDays = days;
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        foreach (Action handler in Delegate.EnumerateInvocationList(StateChanged))
        {
            try { handler(); }
            catch (Exception ex) { Log.Warning(ex, "许可状态订阅者执行失败"); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) != 0) return;
        _disposed = true;
        _initialization.TrySetResult();
        if (_backend is null)
        {
            StateChanged = null;
            return;
        }
        if (_dispatcher.CheckAccess()) DisposeOnUi();
        else
        {
            try { _dispatcher.Post(DisposeOnUi); }
            catch (Exception ex) { Log.Warning(ex, "许可服务清理调度失败"); }
        }
    }

    private void DisposeOnUi()
    {
        StateChanged = null;
        DisposeResource(ref _refreshTimer);
        DisposeResource(ref _trialTimer);
        if (_backend is null) return;
        try { _backend.OfflineLicensesChanged -= OnOfflineLicensesChanged; }
        catch (Exception ex) { Log.Warning(ex, "解除 Store 许可订阅失败"); }
        try { _backend.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "Store 许可后端清理失败"); }
    }

    private static void DisposeResource(ref IDisposable? resource)
    {
        IDisposable? current = resource;
        resource = null;
        try { current?.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "许可计时器清理失败"); }
    }

    private sealed class UnrestrictedLicenseService : ILicenseService
    {
        public AppLicenseState State => AppLicenseState.StoreUnavailable;
        public bool IsInitialized => true;
        public bool IsRestricted => false;
        public bool CanPurchase => false;
        public int? TrialRemainingDays => null;
        public DateTimeOffset? TrialExpiration => null;
        public event Action? StateChanged { add { } remove { } }
        public bool CanUseFeature(LicenseFeature feature) => true;
        public bool IsFeatureRestricted(LicenseFeature feature) => false;
        public void ThrowIfRestricted(LicenseFeature feature) { }
        public Task EnsureFeatureAsync(LicenseFeature feature, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public Task InitializeAsync(nint ownerHandle) => Task.CompletedTask;
        public Task RefreshAsync() => Task.CompletedTask;
        public Task PurchaseAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
