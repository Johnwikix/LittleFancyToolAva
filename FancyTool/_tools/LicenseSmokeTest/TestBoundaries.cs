using System.Collections.Concurrent;
using FancyToolAva.Services;

namespace LicenseSmokeTest;

/// <summary>只替换 Store 外部边界，不复制许可状态机。</summary>
internal sealed class StoreProbe : IStoreLicenseBackend
{
    private readonly ConcurrentQueue<Func<Task<StoreLicenseSnapshot>>> _queries = new();
    private int _queryCalls;
    private int _activeQueries;
    public event Action? OfflineLicensesChanged;
    public bool Available { get; set; } = true;
    public StoreLicenseSnapshot Current { get; set; } = new(false, false, null);
    public Func<Task>? Purchase { get; set; }
    public int InitializeCalls { get; private set; }
    public int QueryCalls => Volatile.Read(ref _queryCalls);
    public int PurchaseCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public int MaxConcurrentQueries { get; private set; }
    public List<int> ApiThreads { get; } = [];

    public bool TryInitialize(nint ownerHandle)
    {
        ApiThreads.Add(Environment.CurrentManagedThreadId);
        InitializeCalls++;
        return Available;
    }

    public TaskCompletionSource<StoreLicenseSnapshot> DelayQuery()
    {
        var completion = new TaskCompletionSource<StoreLicenseSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queries.Enqueue(() => completion.Task);
        return completion;
    }

    public void FailNextQuery() => _queries.Enqueue(() => Task.FromException<StoreLicenseSnapshot>(new IOException("模拟 Store 查询失败")));

    public async Task<StoreLicenseSnapshot> GetLicenseAsync()
    {
        ApiThreads.Add(Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref _queryCalls);
        int active = Interlocked.Increment(ref _activeQueries);
        MaxConcurrentQueries = Math.Max(MaxConcurrentQueries, active);
        try { return await (_queries.TryDequeue(out var query) ? query() : Task.FromResult(Current)); }
        finally { Interlocked.Decrement(ref _activeQueries); }
    }

    public Task PurchaseAsync()
    {
        ApiThreads.Add(Environment.CurrentManagedThreadId);
        PurchaseCalls++;
        return Purchase?.Invoke() ?? Task.CompletedTask;
    }

    public void RaiseOfflineChanged() => OfflineLicensesChanged?.Invoke();
    public void Dispose() => DisposeCalls++;
}

internal sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>真实单线程消息循环；只替换 UI 平台和计时器触发源。</summary>
internal sealed class TestDispatcher : ILicenseUiDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly List<TestTimer> _timers = [];
    private readonly ConcurrentQueue<Exception> _unhandled = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int ThreadId { get; private set; }
    public TestDispatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "LicenseSmokeTest UI" };
        _thread.Start();
        _started.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        ThreadId = Environment.CurrentManagedThreadId;
        SynchronizationContext.SetSynchronizationContext(new QueueSynchronizationContext(this));
        _started.SetResult();
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) { _unhandled.Enqueue(ex); }
        }
    }

    public bool CheckAccess() => Environment.CurrentManagedThreadId == ThreadId;

    public Task InvokeAsync(Func<Task> action)
    {
        if (CheckAccess()) return action();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try { await action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    public void Post(Action action) => _queue.Add(action);

    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        if (!CheckAccess()) throw new InvalidOperationException("计时器必须在 UI 线程创建。");
        var timer = new TestTimer(interval, tick);
        _timers.Add(timer);
        return timer;
    }

    public Task FireAsync(TimeSpan interval) => InvokeAsync(() =>
    {
        foreach (var timer in _timers.Where(timer => !timer.Disposed && timer.Interval == interval).ToArray()) timer.Tick();
        return Task.CompletedTask;
    });

    public async Task<int> ActiveTimersAsync(TimeSpan? interval = null)
    {
        int count = 0;
        await InvokeAsync(() =>
        {
            count = _timers.Count(timer => !timer.Disposed && (!interval.HasValue || timer.Interval == interval));
            return Task.CompletedTask;
        });
        return count;
    }

    public async Task DrainAsync()
    {
        await InvokeAsync(() => Task.CompletedTask);
        if (_unhandled.TryDequeue(out var failure)) throw new InvalidOperationException("UI 消息循环存在未观察异常。", failure);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("许可 UI 线程未退出。");
        _queue.Dispose();
        if (_unhandled.TryDequeue(out var failure)) throw new InvalidOperationException("UI 消息循环存在未观察异常。", failure);
    }

    private sealed class QueueSynchronizationContext(TestDispatcher owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => owner.Post(() => callback(state));
        public override SynchronizationContext CreateCopy() => this;
    }

    private sealed class TestTimer(TimeSpan interval, Action tick) : IDisposable
    {
        public TimeSpan Interval { get; } = interval;
        public Action Tick { get; } = tick;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
