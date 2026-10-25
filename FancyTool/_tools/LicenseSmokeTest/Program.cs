using System.Diagnostics;
using System.Text.Json;
using FancyToolAva.Services;

namespace LicenseSmokeTest;

internal static partial class Program
{
    private static bool _expectMsix;
    private static readonly DateTimeOffset Epoch = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly LicenseFeature[] AiFeatures =
    [LicenseFeature.ImageSuperResolution, LicenseFeature.VideoSuperResolution, LicenseFeature.AiFrameInterpolation, LicenseFeature.AllAi];

    private static async Task<int> Main(string[] args)
    {
        _expectMsix = args.Contains("--expect-msix");
        bool buildPolicyOnly = args.Contains("--build-policy-only");
        string workDirectory = Path.GetFullPath(args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)) ?? ".qa/license-smoke");
        Directory.CreateDirectory(workDirectory);
        Environment.SetEnvironmentVariable("FANCYTOOL_DATA_DIRECTORY", Path.Combine(workDirectory, "app-data-" + Guid.NewGuid().ToString("N")));
        AppPaths.InitializeModelsDirectory(null);
        var results = new List<TestResult>();
        (string Name, Func<Task> Run)[] cases =
        [
            ("compiled-build-policy-and-default-constructor", CompiledBuildPolicyAsync),
            ("unrestricted-bypass", UnrestrictedAsync),
            ("backend-unavailable-no-store-api", UnavailableBackendAsync),
            ("initialization-wait-and-cancellation", InitializationWaitAsync),
            ("inactive-full-policy-and-ui-thread", StateAndThreadAsync),
            ("first-query-failure-fail-open", InitialFailureAsync),
            ("later-query-failure-retains-last-known-state", LaterFailureAsync),
            ("pending-refresh-single-flight", PendingRefreshAsync),
            ("synchronous-query-refresh-regression", SynchronousRefreshAsync),
            ("offline-event-refresh-from-worker", OfflineEventAsync),
            ("purchase-old-query-race", PurchaseRaceAsync),
            ("canceled-purchase-does-not-unlock", CanceledPurchaseAsync),
            ("trial-countdown-store-confirmed-expiration", TrialCountdownAsync),
            ("dispose-rejects-late-query", DisposeLateQueryAsync),
            ("production-ai-entry-guards", () => ProductionGuardsAsync(workDirectory)),
            ("queued-ai-entry-rechecks-license", QueuedGuardsAsync)
        ];
        if (buildPolicyOnly)
            cases = cases.Where(test => test.Name == "compiled-build-policy-and-default-constructor").ToArray();
        foreach (var test in cases)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await test.Run().WaitAsync(TimeSpan.FromSeconds(20));
                results.Add(new(test.Name, true, stopwatch.Elapsed.TotalMilliseconds, null));
                Console.WriteLine("PASS " + test.Name);
            }
            catch (Exception ex)
            {
                results.Add(new(test.Name, false, stopwatch.Elapsed.TotalMilliseconds, ex.ToString()));
                Console.Error.WriteLine("FAIL " + test.Name + ": " + ex);
            }
        }
        File.WriteAllText(Path.Combine(workDirectory, "results.json"), JsonSerializer.Serialize(new
        {
            ExecutedAt = DateTimeOffset.UtcNow,
            Environment = new { System.Environment.OSVersion, System.Environment.Version, System.Environment.ProcessorCount },
            Scope = buildPolicyOnly
                ? "仅检查编译产物和默认许可旁路，不调用 Skia/推理原生接口或真实 Store；Windows host 加载 Linux 交叉编译程序集不代表 Linux 实机验证。"
                : "生产许可状态机及入口守卫；Store/时钟/UI调度为边界替身，不代表真实商店交易验收。",
            BuildPolicyOnly = buildPolicyOnly,
            ExpectedMsixBuild = _expectMsix,
            Passed = results.Count(result => result.Passed), Total = results.Count, Results = results
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"License smoke: {results.Count(result => result.Passed)}/{results.Count} passed.");
        return results.All(result => result.Passed) ? 0 : 1;
    }

    private static async Task CompiledBuildPolicyAsync()
    {
        // 检查的是实际主程序集的条件编译产物，不以 fake backend 冒充渠道验证。
        bool backendPresent = typeof(LicenseService).Assembly.GetType("FancyToolAva.Services.WindowsStoreLicenseBackend") != null;
        Check(backendPresent == (_expectMsix && OperatingSystem.IsWindows()), "实际编译渠道的 Store 后端存在性与预期不符。");
        if (_expectMsix) return;
        using var service = new LicenseService();
        Check(service.IsInitialized && !service.IsRestricted && !service.CanPurchase,
            "非 MSIX 的 public 默认构造仍等待 Store 许可。");
        await service.InitializeAsync(0);
        await service.RefreshAsync();
        await service.PurchaseAsync();
        await service.EnsureFeatureAsync(LicenseFeature.AllAi);
        Check(service.State == AppLicenseState.StoreUnavailable && service.CanUseFeature(LicenseFeature.AllAi),
            "非 MSIX 默认实例触发了商店门控。");
    }

    private static async Task UnrestrictedAsync()
    {
        using var dispatcher = new TestDispatcher();
        using var service = new LicenseService(null, new TestTimeProvider(Epoch), dispatcher);
        await service.InitializeAsync(0);
        Check(service.IsInitialized && service.State == AppLicenseState.StoreUnavailable && !service.IsRestricted && !service.CanPurchase,
            "非商店实例未直接放行。");
        foreach (var feature in AiFeatures)
        {
            Check(service.CanUseFeature(feature) && !service.IsFeatureRestricted(feature), "非商店功能受到限制。");
            service.ThrowIfRestricted(feature);
            await service.EnsureFeatureAsync(feature);
            Check(LicenseService.Unrestricted.CanUseFeature(feature), "独立服务默认旁路实例受限。");
        }
        await service.RefreshAsync();
        await service.PurchaseAsync();
        Check(await dispatcher.ActiveTimersAsync() == 0, "非商店实例启动了许可计时器。");
    }

    private static async Task UnavailableBackendAsync()
    {
        using var dispatcher = new TestDispatcher();
        var backend = new StoreProbe { Available = false };
        using var service = new LicenseService(backend, new TestTimeProvider(Epoch), dispatcher);
        await service.InitializeAsync((nint)1234);
        await service.RefreshAsync();
        await service.PurchaseAsync();
        backend.RaiseOfflineChanged();
        await dispatcher.DrainAsync();
        Check(service.IsInitialized && !service.IsRestricted && !service.CanPurchase, "Store 不可用时未放行。");
        Check(backend.InitializeCalls == 1 && backend.QueryCalls == 0 && backend.PurchaseCalls == 0,
            "无法初始化 Store 时仍调用查询或购买 API。");
        Check(await dispatcher.ActiveTimersAsync() == 0, "Store 不可用时启动了计时器。");
    }

    private static async Task InitializationWaitAsync()
    {
        using var dispatcher = new TestDispatcher();
        var backend = new StoreProbe();
        using var service = new LicenseService(backend, new TestTimeProvider(Epoch), dispatcher);
        await service.EnsureFeatureAsync(LicenseFeature.None).WaitAsync(TimeSpan.FromSeconds(1));
        service.ThrowIfRestricted(LicenseFeature.None);
        await ThrowsAsync<InvalidOperationException>(() => { service.ThrowIfRestricted(LicenseFeature.ImageSuperResolution); return Task.CompletedTask; });
        using var cancellation = new CancellationTokenSource();
        var waiter = service.EnsureFeatureAsync(LicenseFeature.ImageSuperResolution, cancellation.Token);
        await dispatcher.DrainAsync();
        Check(!waiter.IsCompleted && backend.QueryCalls == 0, "初始化之前 AI 操作提前通过或擅自查询 Store。");
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => waiter);
        await service.InitializeAsync((nint)1234);
        await ThrowsAsync<LicenseRequiredException>(() => service.EnsureFeatureAsync(LicenseFeature.ImageSuperResolution));
        Check(backend.QueryCalls == 1, "取消调用方等待取消了共享初始化或触发重复查询。");
    }

    private static async Task StateAndThreadAsync()
    {
        using var dispatcher = new TestDispatcher();
        var backend = new StoreProbe();
        using var service = new LicenseService(backend, new TestTimeProvider(Epoch), dispatcher);
        var events = new List<int>();
        service.StateChanged += () => throw new InvalidOperationException("模拟一个失败的 UI 订阅者");
        service.StateChanged += () => events.Add(Environment.CurrentManagedThreadId);
        await Task.Run(() => service.InitializeAsync((nint)1234));
        Check(service.State == AppLicenseState.LicenseInactive && service.IsRestricted && service.CanPurchase, "未购状态不正确。");
        foreach (var feature in AiFeatures)
        {
            Check(!service.CanUseFeature(feature) && service.IsFeatureRestricted(feature), "未购 AI 功能获得权限。");
            await ThrowsAsync<LicenseRequiredException>(() => service.EnsureFeatureAsync(feature));
            await ThrowsAsync<LicenseRequiredException>(() => { service.ThrowIfRestricted(feature); return Task.CompletedTask; });
        }
        backend.Current = new(true, false, null);
        await Task.Run(service.RefreshAsync);
        Check(service.State == AppLicenseState.FullLicense && !service.IsRestricted, "已购状态未恢复权限。");
        foreach (var feature in AiFeatures) await service.EnsureFeatureAsync(feature);
        Check(events.Count >= 2 && events.All(thread => thread == dispatcher.ThreadId), "许可事件未归一到 UI 线程，或订阅异常阻断了其他订阅者。");
        Check(backend.ApiThreads.All(thread => thread == dispatcher.ThreadId), "Store API 未在 UI 线程调用。");
        await dispatcher.DrainAsync();
    }

    private static async Task InitialFailureAsync()
    {
        using var dispatcher = new TestDispatcher();
        var backend = new StoreProbe();
        backend.FailNextQuery();
        using var service = new LicenseService(backend, new TestTimeProvider(Epoch), dispatcher);
        await service.InitializeAsync((nint)1234);
        Check(service.IsInitialized && service.State == AppLicenseState.StoreUnavailable && !service.IsRestricted,
            "首次查询失败未遵循参考项目的放行策略。");
        await service.EnsureFeatureAsync(LicenseFeature.AllAi);
        await service.RefreshAsync();
        Check(service.State == AppLicenseState.LicenseInactive && service.IsRestricted, "查询恢复后未收回未购权限。");
    }

    private static async Task LaterFailureAsync()
    {
        foreach (var snapshot in new[] { new StoreLicenseSnapshot(false, false, null), new(true, false, null), new(true, true, Epoch.AddDays(2)) })
        {
            using var dispatcher = new TestDispatcher();
            var backend = new StoreProbe { Current = snapshot };
            using var service = new LicenseService(backend, new TestTimeProvider(Epoch), dispatcher);
            await service.InitializeAsync((nint)1234);
            var state = service.State;
            bool restricted = service.IsRestricted;
            backend.FailNextQuery();
            await service.RefreshAsync();
            Check(service.State == state && service.IsRestricted == restricted, "后续失败改变最后一次可靠许可结果。");
        }
    }

    private static async Task PendingRefreshAsync()
    {
        using var dispatcher = new TestDispatcher();
        var backend = new StoreProbe();
        var first = backend.DelayQuery();
        var second = backend.DelayQuery();
        using var service = new LicenseService(backend, new TestTimeProvider(Epoch), dispatcher);
        var initialize = service.InitializeAsync((nint)1234);
        await EventuallyAsync(() => backend.QueryCalls == 1, "初始化查询未启动。");
        var refreshes = Enumerable.Range(0, 8).Select(_ => service.RefreshAsync()).ToArray();
        await dispatcher.DrainAsync();
        first.SetResult(new(false, false, null));
        await EventuallyAsync(() => backend.QueryCalls == 2, "在途刷新丢弃了 pending 补查。");
        second.SetResult(new(true, false, null));
        await Task.WhenAll(refreshes.Append(initialize));
        Check(backend.QueryCalls == 2 && backend.MaxConcurrentQueries == 1 && service.State == AppLicenseState.FullLicense,
            "刷新未合并为串行补查，或最终结果被旧查询覆盖。");
    }

    private static async Task SynchronousRefreshAsync()
    {
        using var dispatcher = new TestDispatcher();
        var backend = new StoreProbe();
        using var service = new LicenseService(backend, new TestTimeProvider(Epoch), dispatcher);
        await service.InitializeAsync((nint)1234);
        for (int index = 0; index < 20; index++)
        {
            bool active = (index & 1) == 0;
            backend.Current = new(active, false, null);
            await service.RefreshAsync();
            Check(service.State == (active ? AppLicenseState.FullLicense : AppLicenseState.LicenseInactive),
                "同步完成的 Store 查询使单飞任务停留在旧状态。");
        }
        Check(backend.QueryCalls == 21 && backend.MaxConcurrentQueries == 1, "同步刷新发生漏查或并发。");
    }

    private static async Task OfflineEventAsync()
    {
        using var dispatcher = new TestDispatcher();
        var backend = new StoreProbe();
        using var service = new LicenseService(backend, new TestTimeProvider(Epoch), dispatcher);
        await service.InitializeAsync((nint)1234);
        backend.Current = new(true, false, null);
        await Task.Run(backend.RaiseOfflineChanged);
        await EventuallyAsync(() => service.State == AppLicenseState.FullLicense, "后台 OfflineLicensesChanged 未刷新许可。");
        Check(backend.ApiThreads.All(thread => thread == dispatcher.ThreadId), "后台事件直接跨线程访问 Store。");
    }

    private static async Task PurchaseRaceAsync()
    {
        using var dispatcher = new TestDispatcher();
        var backend = new StoreProbe();
        using var service = new LicenseService(backend, new TestTimeProvider(Epoch), dispatcher);
        await service.InitializeAsync((nint)1234);
        var stale = backend.DelayQuery();
        var fresh = backend.DelayQuery();
        var query = service.RefreshAsync();
        await EventuallyAsync(() => backend.QueryCalls == 2, "购买前旧查询未启动。");
        var purchaseCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Purchase = () => purchaseCompletion.Task;
        var purchase = service.PurchaseAsync();
        await EventuallyAsync(() => backend.PurchaseCalls == 1, "购买流程未启动。");
        Check(service.IsRestricted, "尚未确认 Store 授权便解锁了功能。");
        purchaseCompletion.SetResult();
        await dispatcher.DrainAsync();
        stale.SetResult(new(false, false, null));
        await EventuallyAsync(() => backend.QueryCalls == 3, "购买完成未强制补查最新授权。");
        fresh.SetResult(new(true, false, null));
        await Task.WhenAll(query, purchase);
        Check(service.State == AppLicenseState.FullLicense && !service.IsRestricted && backend.MaxConcurrentQueries == 1,
            "购买成功被旧未购查询覆盖，或查询并发执行。");
    }

    private static async Task CanceledPurchaseAsync()
    {
        using var dispatcher = new TestDispatcher();
        var backend = new StoreProbe { Purchase = () => Task.FromException(new OperationCanceledException("用户取消购买")) };
        using var service = new LicenseService(backend, new TestTimeProvider(Epoch), dispatcher);
        await service.InitializeAsync((nint)1234);
        try { await service.PurchaseAsync(); }
        catch (OperationCanceledException) { }
        Check(service.State == AppLicenseState.LicenseInactive && service.IsRestricted && backend.PurchaseCalls == 1,
            "取消购买解锁了功能。");
    }

    private static async Task TrialCountdownAsync()
    {
        using var dispatcher = new TestDispatcher();
        var clock = new TestTimeProvider(Epoch);
        DateTimeOffset expiration = Epoch.AddHours(25);
        var backend = new StoreProbe { Current = new(true, true, expiration) };
        using var service = new LicenseService(backend, clock, dispatcher);
        await service.InitializeAsync((nint)1234);
        Check(service.State == AppLicenseState.TrialActive && !service.IsRestricted && service.TrialRemainingDays == 2
            && service.TrialExpiration == expiration, "有效试用状态/向上取整剩余天数不正确。");
        Check(await dispatcher.ActiveTimersAsync(TimeSpan.FromMinutes(30)) == 1
            && await dispatcher.ActiveTimersAsync(TimeSpan.FromMinutes(1)) == 1, "许可刷新/试用倒计时计时器不正确。");
        clock.Now = Epoch.AddHours(2);
        await dispatcher.FireAsync(TimeSpan.FromMinutes(1));
        Check(service.TrialRemainingDays == 1, "试用倒计时未更新。");
        clock.Now = expiration.AddSeconds(1);
        await dispatcher.FireAsync(TimeSpan.FromMinutes(1));
        Check(service.TrialRemainingDays == 0 && service.State == AppLicenseState.TrialActive && !service.IsRestricted,
            "本地时间到期直接收回权限，偏离 Store 确认策略。");
        backend.Current = new(false, true, expiration);
        await dispatcher.FireAsync(TimeSpan.FromMinutes(30));
        await EventuallyAsync(() => service.State == AppLicenseState.LicenseInactive, "Store 确认失效后仍保持试用权限。");
        Check(service.IsRestricted && await dispatcher.ActiveTimersAsync(TimeSpan.FromMinutes(1)) == 0,
            "退出试用后倒计时未停止。");
        service.Dispose();
        await dispatcher.DrainAsync();
        Check(await dispatcher.ActiveTimersAsync() == 0, "释放许可服务后仍有计时器。");
    }

    private static async Task DisposeLateQueryAsync()
    {
        using var dispatcher = new TestDispatcher();
        var backend = new StoreProbe();
        var delayed = backend.DelayQuery();
        var service = new LicenseService(backend, new TestTimeProvider(Epoch), dispatcher);
        int notifications = 0;
        service.StateChanged += () => notifications++;
        var initialize = service.InitializeAsync((nint)1234);
        var waiting = service.EnsureFeatureAsync(LicenseFeature.AllAi);
        await EventuallyAsync(() => backend.QueryCalls == 1, "释放测试查询未启动。");
        await dispatcher.InvokeAsync(() => { service.Dispose(); return Task.CompletedTask; });
        int atDispose = notifications;
        await ThrowsAsync<ObjectDisposedException>(() => waiting);
        delayed.SetResult(new(true, false, null));
        try { await initialize; } catch (ObjectDisposedException) { }
        await dispatcher.DrainAsync();
        service.Dispose();
        Check(notifications == atDispose && backend.DisposeCalls == 1 && await dispatcher.ActiveTimersAsync() == 0,
            "释放后的迟到查询发布状态、重复释放 Store，或计时器残留。");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("预期异常：" + typeof(T).Name);
    }

    private static async Task EventuallyAsync(Func<bool> condition, string message)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException(message);
            await Task.Delay(5);
        }
    }

    private sealed record TestResult(string Name, bool Passed, double ElapsedMilliseconds, string? Error);
}
