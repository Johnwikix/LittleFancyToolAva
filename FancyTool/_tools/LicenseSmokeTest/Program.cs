using System.Text.Json;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Diagnostics;
using System.Net.Sockets;
using System.Xml.Linq;
using Avalonia.Threading;
using FancyToolAva;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FancyToolAva.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LicenseSmokeTest;

internal static class Program
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

    [STAThread]
    private static int Main(string[] args)
    {
        // 只有独立烟测宿主读取此变量；主程序不编译 _tools，也没有许可覆盖入口。
        if (Environment.GetEnvironmentVariable("FANCYTOOL_LICENSE_SMOKE_PROCESS") is { } probeDirectory)
            return RunProcessProbe(probeDirectory);
        string output = Path.GetFullPath(args.FirstOrDefault(arg => !arg.StartsWith("--")) ?? ".qa/msix-license-2026-10-31/smoke");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("FANCYTOOL_DATA_DIRECTORY", Path.Combine(output, "app-data"));
        bool expectMsix = args.Contains("--expect-msix");
        var results = new List<object>();
        int failures = 0;
        using var stop = new CancellationTokenSource();
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
        Dispatcher.UIThread.Post(async () =>
        {
            (string Name, Func<Task> Run)[] cases =
            [
                ("actual-build-channel", () => BuildChannelAsync(expectMsix)),
                ("published-store-package-identity", PublishedStoreIdentityAsync),
                ("wrong-package-identity-locks-without-purchase-claim", PackageMismatchAsync),
                ("settings-license-status-and-purchase-reminder", LicenseStatusAsync),
                ("non-msix-unlocked-no-store-no-timers", NonMsixAsync),
                ("checking-does-not-create-tool-pages", DeferredToolsAsync),
                ("real-service-graph-unlocks-then-revokes-workspace", WorkspaceSessionAsync),
                ("inactive-disables-entire-app", InactiveAsync),
                ("first-query-failure-locks", FirstFailureAsync),
                ("initialization-failure-locks-and-retry-recovers", InitializationFailureAsync),
                ("subsequent-query-failure-revokes-full-license", LaterFailureAsync),
                ("expired-and-missing-trial-deadlines-lock", InvalidTrialsAsync),
                ("trial-expires-without-store-refresh", ExpirationAsync),
                ("clock-rollback-cannot-extend-running-trial", ClockRollbackAsync),
                ("refresh-coalesces-pending-requests-on-ui-thread", RefreshRaceAsync),
                ("offline-license-event-marshals-to-ui-thread", OfflineEventAsync),
                ("purchase-cancel-and-failure-do-not-unlock", PurchaseFailureAsync),
                ("purchase-requeries-and-single-flights", PurchaseAsync),
                ("purchase-waits-for-pending-refresh", PurchaseRefreshRaceAsync),
                ("query-timeout-locks-and-rejects-late-result", QueryTimeoutAsync),
                ("monitoring-failure-cannot-unlock-on-retry", MonitoringFailureAsync),
                ("dispose-rejects-late-query-and-stops-monitors", DisposalAsync),
                ("real-process-restart-stops-workers-and-releases-handles", () => ProcessBoundaryAsync(output))
            ];
            foreach (var test in cases)
            {
                try
                {
                    await test.Run().WaitAsync(TimeSpan.FromSeconds(10));
                    results.Add(new { test.Name, Passed = true, Error = (string?)null });
                    Console.WriteLine("PASS " + test.Name);
                }
                catch (Exception ex)
                {
                    failures++;
                    results.Add(new { test.Name, Passed = false, Error = ex.ToString() });
                    Console.WriteLine("FAIL " + test.Name + ": " + ex);
                }
            }
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                Date = DateTimeOffset.UtcNow,
                Environment.OSVersion,
                Runtime = Environment.Version.ToString(),
                Scope = "真实生产许可服务、主 VM 的延迟工具构造边界及 Avalonia 调度；Store 和计时器为可控边界，不代表真实商店交易。",
                ExpectedMsix = expectMsix, Total = cases.Length, Passed = cases.Length - failures, Results = results
            }, new JsonSerializerOptions { WriteIndented = true }));
            stop.Cancel();
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        return failures == 0 ? 0 : 1;
    }

    private static async Task BuildChannelAsync(bool expectMsix)
    {
        bool backendPresent = typeof(LicenseService).Assembly.GetType("FancyToolAva.Services.WindowsStoreLicenseBackend") != null;
        Check(backendPresent == expectMsix, "主程序集的实际渠道编译与预期不符");
        using var service = new LicenseService();
        Check(service.CanUseApplication == !expectMsix, "默认构造初始状态不符");
        if (expectMsix)
        {
            await service.InitializeAsync(0); // 无窗口/包身份的真实 MSIX 默认服务必须锁定。
            Check(!service.CanUseApplication && service.State == AppLicenseState.VerificationFailed, "MSIX 缺少身份时放行");
        }
        else
        {
            await service.InitializeAsync(0);
            await service.RefreshAsync();
            await service.PurchaseAsync();
            Check(service.CanUseApplication && !service.CanPurchase && service.State == AppLicenseState.Unrestricted, "非 MSIX 被门控");
        }
    }

    private static async Task NonMsixAsync()
    {
        var scheduler = new Scheduler();
        using var service = new LicenseService(null, new Clock(), scheduler);
        await service.InitializeAsync(0);
        await service.RefreshAsync();
        await service.PurchaseAsync();
        Check(service.CanUseApplication && !service.CanPurchase && scheduler.Timers.Count == 0, "非 MSIX 启动了授权工作");
    }

    private static Task PublishedStoreIdentityAsync()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "FancyToolAva.Msix", "Package.appxmanifest")))
            repository = repository.Parent;
        Check(repository != null, "无法定位实际打包清单");
        string packaging = Path.Combine(repository!.FullName, "FancyToolAva.Msix");
        Check(StoreAppIdentity.PackageName == "SennpaiStudio.---modbus"
            && StoreAppIdentity.PackageFamilyName == "SennpaiStudio.---modbus_z79ft30j24epr"
            && StoreAppIdentity.ProductId == "9P543GHQQKVK", "运行期校验的商品身份与实际已发布商品不符");
        var manifest = XDocument.Load(Path.Combine(packaging, "Package.appxmanifest"));
        var identity = manifest.Root!.Elements().Single(element => element.Name.LocalName == "Identity");
        // 期望值来自 9P543GHQQKVK 的 Microsoft Store 公开目录，不以两份本地 XML 自洽作为正确性证据。
        Check((string?)identity.Attribute("Name") == "SennpaiStudio.---modbus", "MSIX 包名与已发布商品的实际包身份不符");
        Check((string?)identity.Attribute("Publisher") == "CN=CF259542-70C0-4852-9523-066A80212473", "MSIX 发布者与商店身份不符");
        var association = XDocument.Load(Path.Combine(packaging, "Package.StoreAssociation.xml"));
        var reserved = association.Descendants().Single(element => element.Name.LocalName == "ProductReservedInfo");
        Check(reserved.Elements().Single(element => element.Name.LocalName == "MainPackageIdentityName").Value == "SennpaiStudio.---modbus",
            "VS 商店关联仍指向另一份包身份");
        return Task.CompletedTask;
    }

    private static async Task DeferredToolsAsync()
    {
        var store = new Store();
        var scheduler = new Scheduler();
        using var service = new LicenseService(store, new Clock(), scheduler);
        using var license = new LicenseViewModel(service);
        var provider = new RejectingProvider();
        using var main = new MainWindowViewModel(new AppObserveModel(), provider, service, license);
        Check(!main.IsWorkspaceEnabled && main.Pages.Count == 0 && main.FooterPages.Count == 0 && provider.Calls == 0,
            "初查前构造了工具页面或依赖");
        await service.InitializeAsync(1);
        Check(!main.IsWorkspaceEnabled && license.IsLocked && provider.Calls == 0 && main.Pages.Count == 0,
            "拒绝授权后构造了工具页面或依赖");
    }

    private static async Task InactiveAsync()
    {
        using var fixture = new Fixture();
        await fixture.Service.InitializeAsync(1);
        Check(!fixture.Service.CanUseApplication && fixture.Service.CanPurchase && fixture.Service.State == AppLicenseState.LicenseInactive,
            "非活跃许可未全局锁定");
        fixture.Store.Current = new(true, false, null);
        await fixture.Service.RefreshAsync();
        Check(fixture.Service.CanUseApplication && !fixture.Service.CanPurchase, "正式许可未恢复权限");
    }

    private static async Task WorkspaceSessionAsync()
    {
        using var fixture = new Fixture();
        LocalizationStrings.Initialize(new CultureInfo("en-US"));
        var services = new ServiceCollection();
        // 仅测试宿主读取真实生产 DI 注册，避免另写一份工具依赖图自证；生产代码没有测试开关。
        typeof(App).GetMethod("ConfigureServices", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new App(), [services]);
        services.AddSingleton<ILicenseService>(fixture.Service);
        services.AddHttpClient<ModelDownloadService>().ConfigurePrimaryHttpMessageHandler(() => new NoDownloadHandler());
        using var provider = services.BuildServiceProvider();
        var main = provider.GetRequiredService<MainWindowViewModel>();
        Check(!main.HasStartedTools && main.Pages.Count == 0, "生产 DI 在初查前创建了页面");
        fixture.Store.Current = new(true, true, Epoch.AddSeconds(3));
        await fixture.Service.InitializeAsync(1);
        Check(main.HasStartedTools && main.IsWorkspaceEnabled && main.Pages.Count == 6 && main.FooterPages.Count == 1,
            "通过许可后实际工具图未完整创建");
        Check(ReferenceEquals(provider.GetRequiredService<SettingsViewModel>().License, main.License), "设置页没有复用唯一许可状态源");
        await provider.GetRequiredService<ISuperResolutionService>().WarmupTask;
        await provider.GetRequiredService<IFfmpegService>().ValidateAsync();
        int revoked = 0;
        main.WorkspaceRevoked += () =>
        {
            Check(!main.IsWorkspaceEnabled && main.License.IsLocked, "通知结束会话前仍开放了工具");
            revoked++;
        };
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        fixture.Scheduler.TickSeconds();
        Check(revoked == 1 && fixture.Store.Queries == 1, "运行中到期未立即请求结束工具会话");
    }

    private static async Task FirstFailureAsync()
    {
        using var fixture = new Fixture();
        fixture.Store.Query = () => Task.FromException<StoreLicenseSnapshot>(new IOException("offline"));
        await fixture.Service.InitializeAsync(1);
        Check(!fixture.Service.CanUseApplication && fixture.Service.CanPurchase && fixture.Service.State == AppLicenseState.VerificationFailed,
            "首次失败存在放行路径");
    }

    private static async Task PackageMismatchAsync()
    {
        using var fixture = new Fixture();
        fixture.Store.InitializationError = new StoreIdentityMismatchException();
        using var viewModel = new LicenseViewModel(fixture.Service);
        LocalizationStrings.Initialize(new CultureInfo("en-US"));
        await fixture.Service.InitializeAsync(1);
        Check(fixture.Service.State == AppLicenseState.PackageIdentityMismatch && !fixture.Service.CanUseApplication
            && fixture.Store.Queries == 0, "错误商品身份仍查询或放行了许可");
        Check(viewModel.TitleText == LocalizationStrings.Current["License.PackageMismatchTitle"] && !viewModel.ShowPurchase,
            "把包身份不匹配误报为需要购买");
    }

    private static async Task LicenseStatusAsync()
    {
        LocalizationStrings.Initialize(new CultureInfo("en-US"));
        using var fixture = new Fixture();
        using var viewModel = new LicenseViewModel(fixture.Service);
        int statusChanges = 0;
        viewModel.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(viewModel.StatusText)) statusChanges++; };
        fixture.Store.Current = new(true, true, Epoch.AddDays(3));
        await fixture.Service.InitializeAsync(1);
        Check(viewModel.StatusText == LocalizationStrings.Current["License.TrialActive"] && viewModel.ShowPurchase
            && viewModel.ShowTrialExpiration && viewModel.TrialExpirationText.Length > 0, "设置页未显示有效试用及购买入口");
        fixture.Store.Current = new(true, false, null);
        await fixture.Service.RefreshAsync();
        Check(viewModel.StatusText == LocalizationStrings.Current["License.FullLicense"] && !viewModel.ShowPurchase
            && !viewModel.ShowTrialExpiration && statusChanges >= 2, "购买确认后设置页没有实时刷新为完整版");
        fixture.Store.Query = () => Task.FromException<StoreLicenseSnapshot>(new IOException("offline"));
        await fixture.Service.RefreshAsync();
        Check(viewModel.TitleText == LocalizationStrings.Current["License.VerificationTitle"] && !viewModel.ShowPurchase,
            "把许可查询失败误报为未购买");
        using var ordinary = new LicenseService(null, new Clock(), new Scheduler());
        using var ordinaryViewModel = new LicenseViewModel(ordinary);
        Check(ordinaryViewModel.StatusText == LocalizationStrings.Current["License.Unrestricted"] && !ordinaryViewModel.ShowPurchase,
            "非 MSIX 完全解锁状态不正确");
    }

    private static async Task InitializationFailureAsync()
    {
        using var fixture = new Fixture();
        fixture.Store.FailInitialize = true;
        await fixture.Service.InitializeAsync(1);
        Check(!fixture.Service.CanUseApplication && fixture.Store.Queries == 0 && fixture.Service.CanPurchase, "初始化失败放行");
        fixture.Store.FailInitialize = false;
        fixture.Store.Current = new(true, false, null);
        await fixture.Service.RefreshAsync();
        Check(fixture.Service.CanUseApplication && fixture.Store.Initializations == 2, "初始化失败无法恢复");
    }

    private static async Task LaterFailureAsync()
    {
        using var fixture = new Fixture();
        fixture.Store.Current = new(true, false, null);
        await fixture.Service.InitializeAsync(1);
        fixture.Store.Query = () => Task.FromException<StoreLicenseSnapshot>(new IOException("offline"));
        await fixture.Service.RefreshAsync();
        Check(!fixture.Service.CanUseApplication && fixture.Service.State == AppLicenseState.VerificationFailed, "失败后保留了旧许可权限");
    }

    private static async Task InvalidTrialsAsync()
    {
        foreach (DateTimeOffset? expiration in new DateTimeOffset?[] { null, Epoch, Epoch.AddSeconds(-1) })
        {
            using var fixture = new Fixture();
            fixture.Store.Current = new(true, true, expiration);
            await fixture.Service.InitializeAsync(1);
            Check(!fixture.Service.CanUseApplication && fixture.Service.State == AppLicenseState.LicenseInactive,
                "IsActive=true 的过期/未知到期试用放行");
        }
    }

    private static async Task ExpirationAsync()
    {
        using var fixture = new Fixture();
        fixture.Store.Current = new(true, true, Epoch.AddSeconds(2));
        await fixture.Service.InitializeAsync(1);
        Check(fixture.Service.CanUseApplication, "有效试用未开放");
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Check(!fixture.Service.CanUseApplication, "读取权限时未核对到期边界");
        fixture.Scheduler.TickSeconds();
        Check(fixture.Service.State == AppLicenseState.LicenseInactive && fixture.Store.Queries == 1, "到期依赖 Store 刷新");
    }

    private static async Task ClockRollbackAsync()
    {
        using var fixture = new Fixture();
        fixture.Store.Current = new(true, true, Epoch.AddSeconds(5));
        await fixture.Service.InitializeAsync(1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        fixture.Clock.Utc = Epoch.AddDays(-1);
        await fixture.Service.RefreshAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.Scheduler.TickSeconds();
        Check(!fixture.Service.CanUseApplication, "回拨时钟并刷新延长了同一次试用");
    }

    private static async Task RefreshRaceAsync()
    {
        using var fixture = new Fixture();
        var first = NewCompletion<StoreLicenseSnapshot>();
        var second = NewCompletion<StoreLicenseSnapshot>();
        fixture.Store.Query = () => fixture.Store.Queries == 1 ? first.Task : second.Task;
        var init = fixture.Service.InitializeAsync(1);
        var refreshes = Enumerable.Range(0, 8).Select(_ => fixture.Service.RefreshAsync()).ToArray();
        first.SetResult(new(false, false, null));
        await EventuallyAsync(() => fixture.Store.Queries == 2);
        second.SetResult(new(true, false, null));
        await Task.WhenAll(refreshes.Append(init));
        Check(fixture.Store.Queries == 2 && fixture.Store.MaxConcurrent == 1 && fixture.Service.CanUseApplication, "单飞/补查结果错误");
        Check(fixture.Store.Threads.All(id => id == Environment.CurrentManagedThreadId), "Store 未在 UI 线程调用");
    }

    private static async Task OfflineEventAsync()
    {
        using var fixture = new Fixture();
        await fixture.Service.InitializeAsync(1);
        fixture.Store.Current = new(true, false, null);
        int thread = 0;
        fixture.Service.StateChanged += () => thread = Environment.CurrentManagedThreadId;
        await Task.Run(fixture.Store.RaiseChanged);
        await EventuallyAsync(() => fixture.Service.CanUseApplication);
        Check(thread == Environment.CurrentManagedThreadId, "Store 后台事件未封送到 UI");
    }

    private static async Task PurchaseFailureAsync()
    {
        foreach (bool throws in new[] { false, true })
        {
            using var fixture = new Fixture();
            await fixture.Service.InitializeAsync(1);
            fixture.Store.Purchase = () => throws ? Task.FromException(new IOException("cancel/failure")) : Task.CompletedTask;
            await fixture.Service.PurchaseAsync();
            Check(!fixture.Service.CanUseApplication && fixture.Store.Queries == 2, "取消/失败购买解锁或漏查");
        }
    }

    private static async Task PurchaseAsync()
    {
        using var fixture = new Fixture();
        await fixture.Service.InitializeAsync(1);
        var pending = NewCompletion<bool>();
        fixture.Store.Purchase = async () => { await pending.Task; fixture.Store.Current = new(true, false, null); };
        var purchases = Enumerable.Range(0, 6).Select(_ => fixture.Service.PurchaseAsync()).ToArray();
        Check(!fixture.Service.CanUseApplication && fixture.Store.Purchases == 1, "购买完成前解锁或重复交易");
        pending.SetResult(true);
        await Task.WhenAll(purchases);
        Check(fixture.Service.CanUseApplication && fixture.Store.Queries == 2, "购买后未重新确认许可");
    }

    private static async Task PurchaseRefreshRaceAsync()
    {
        using var fixture = new Fixture();
        await fixture.Service.InitializeAsync(1);
        var pending = NewCompletion<StoreLicenseSnapshot>();
        fixture.Store.Query = () => fixture.Store.Queries == 2 ? pending.Task : Task.FromResult(new StoreLicenseSnapshot(true, false, null));
        var refresh = fixture.Service.RefreshAsync();
        var purchase = fixture.Service.PurchaseAsync();
        pending.SetResult(new(false, false, null));
        await Task.WhenAll(refresh, purchase);
        Check(fixture.Store.Queries == 3 && fixture.Store.MaxConcurrent == 1 && fixture.Service.CanUseApplication,
            "购买后补查被在途旧查询覆盖");
    }

    private static async Task QueryTimeoutAsync()
    {
        var store = new Store();
        var pending = NewCompletion<StoreLicenseSnapshot>();
        store.Query = () => pending.Task;
        using var service = new LicenseService(store, TimeProvider.System, new Scheduler(), TimeSpan.FromMilliseconds(100));
        await service.InitializeAsync(1);
        Check(!service.CanUseApplication && service.State == AppLicenseState.VerificationFailed, "查询超时未锁定");
        await service.RefreshAsync();
        Check(store.Queries == 1, "超时底层调用未完成时叠加了查询");
        pending.SetResult(new(true, false, null));
        await Task.Delay(30);
        Check(!service.CanUseApplication, "超时查询的迟到结果解锁");
        store.Query = null;
        store.Current = new(true, false, null);
        await service.RefreshAsync();
        Check(service.CanUseApplication && store.Queries == 2, "旧查询完成后未通过新查询恢复");
    }

    private static async Task MonitoringFailureAsync()
    {
        var scheduler = new Scheduler { FailTimers = true };
        var store = new Store { Current = new(true, false, null) };
        using var service = new LicenseService(store, new Clock(), scheduler);
        await service.InitializeAsync(1);
        await service.RefreshAsync();
        Check(!service.CanUseApplication && store.Queries == 0, "监控失败后仍可以开启工具");
    }

    private static async Task DisposalAsync()
    {
        using var fixture = new Fixture();
        var pending = NewCompletion<StoreLicenseSnapshot>();
        fixture.Store.Query = () => pending.Task;
        var initialize = fixture.Service.InitializeAsync(1);
        int events = 0;
        fixture.Service.StateChanged += () => events++;
        fixture.Service.Dispose();
        fixture.Service.Dispose();
        pending.SetResult(new(true, false, null));
        await initialize;
        Check(!fixture.Service.CanUseApplication && events == 0 && fixture.Scheduler.Timers.All(timer => timer.Disposed)
            && fixture.Store.Disposals == 1, "释放后迟到结果生效或资源未释放");
    }

    private static int RunProcessProbe(string directory)
    {
        string started = Path.Combine(directory, "started.json");
        if (File.Exists(started))
        {
            File.WriteAllText(Path.Combine(directory, "restarted.json"), JsonSerializer.Serialize(new { Pid = Environment.ProcessId }));
            return 0;
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.ExclusiveAddressUse = true;
        listener.Start();
        using var exclusiveFile = File.Open(Path.Combine(directory, "exclusive-file"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(started, JsonSerializer.Serialize(new { Pid = Environment.ProcessId, Port = ((IPEndPoint)listener.LocalEndpoint).Port }));
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await File.AppendAllTextAsync(Path.Combine(directory, "worker.log"), "tick\r\n");
                await Task.Delay(10);
            }
        });
        // 验证单项失败不会跳过真正的终止；测试的是生产进程边界，不复制退出实现。
        LicenseSessionRestarter.Restart(() => throw new IOException("lock probe"), () => throw new IOException("save probe"));
        return 1;
    }

    private static async Task ProcessBoundaryAsync(string output)
    {
        string directory = Path.Combine(output, "process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        start.Environment["FANCYTOOL_LICENSE_SMOKE_PROCESS"] = directory;
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(process.ExitCode == 0, "旧进程未正常结束");
        await EventuallyAsync(() => File.Exists(Path.Combine(directory, "restarted.json")));
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "started.json")));
        int port = marker.RootElement.GetProperty("Port").GetInt32();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.ExclusiveAddressUse = true;
        listener.Start(); // 原进程仍持有通信句柄时这里会失败。
        using var exclusiveFile = File.Open(Path.Combine(directory, "exclusive-file"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        string worker = Path.Combine(directory, "worker.log");
        long before = File.Exists(worker) ? new FileInfo(worker).Length : 0;
        await Task.Delay(50);
        long after = File.Exists(worker) ? new FileInfo(worker).Length : 0;
        Check(before == after, "旧进程后台任务仍在运行");
    }

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Check(condition(), "异步条件未完成");
    }

    private sealed class Fixture : IDisposable
    {
        public Store Store { get; } = new();
        public Clock Clock { get; } = new();
        public Scheduler Scheduler { get; } = new();
        public LicenseService Service { get; }
        public Fixture() => Service = new(Store, Clock, Scheduler);
        public void Dispose() => Service.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = Epoch;
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan delta) { Utc += delta; _timestamp += delta.Ticks; }
    }

    private sealed class Scheduler : ILicenseDispatcher
    {
        public bool FailTimers { get; set; }
        public List<Lease> Timers { get; } = [];
        public async Task InvokeAsync(Func<Task> action)
        {
            if (Dispatcher.UIThread.CheckAccess()) await action();
            else await Dispatcher.UIThread.InvokeAsync(action);
        }
        public IDisposable StartTimer(TimeSpan interval, Action tick)
        {
            if (FailTimers) throw new IOException("timer unavailable");
            var lease = new Lease(interval, tick);
            Timers.Add(lease);
            return lease;
        }
        public void TickSeconds()
        {
            foreach (var timer in Timers.ToArray())
                if (!timer.Disposed && timer.Interval == TimeSpan.FromSeconds(1)) timer.Tick();
        }
        public sealed class Lease(TimeSpan interval, Action tick) : IDisposable
        {
            public TimeSpan Interval { get; } = interval;
            public bool Disposed { get; private set; }
            public void Tick() => tick();
            public void Dispose() => Disposed = true;
        }
    }

    private sealed class Store : IStoreLicenseBackend
    {
        public StoreLicenseSnapshot Current { get; set; } = new(false, false, null);
        public Func<Task<StoreLicenseSnapshot>>? Query { get; set; }
        public Func<Task>? Purchase { get; set; }
        public bool FailInitialize { get; set; }
        public Exception? InitializationError { get; set; }
        public int Initializations, Queries, Purchases, Disposals, MaxConcurrent;
        private int _concurrent;
        public List<int> Threads { get; } = [];
        public event Action? OfflineLicensesChanged;
        public void Initialize(nint handle)
        {
            Initializations++;
            if (InitializationError != null) throw InitializationError;
            if (FailInitialize) throw new IOException("no package identity");
        }
        public async Task<StoreLicenseSnapshot> GetLicenseAsync()
        {
            Queries++;
            Threads.Add(Environment.CurrentManagedThreadId);
            MaxConcurrent = Math.Max(MaxConcurrent, ++_concurrent);
            try { return Query == null ? Current : await Query(); }
            finally { _concurrent--; }
        }
        public Task PurchaseAsync() { Purchases++; return Purchase?.Invoke() ?? Task.CompletedTask; }
        public void RaiseChanged() => OfflineLicensesChanged?.Invoke();
        public void Dispose() { Disposals++; OfflineLicensesChanged = null; }
    }

    private sealed class RejectingProvider : IServiceProvider
    {
        public int Calls;
        public object? GetService(Type serviceType) { Calls++; throw new InvalidOperationException("禁止提前构造工具 " + serviceType); }
    }

    private sealed class NoDownloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
