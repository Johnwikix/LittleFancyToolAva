using Lang.Avalonia;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace FancyToolAva.Services;

/// <summary>
/// TCP 链路带宽测试：独立于 <see cref="TcpServerService"/> 使用专属 Socket，
/// 不与 TCP 调试通信共享连接。发送端按块大小持续发送指定时长后主动关闭写入，
/// 接收端读到流结束（EOF）即输出汇总并自动结束（单次测试、单连接）。
/// </summary>
public sealed class TcpLinkTestService : IDisposable
{
    private readonly ILogger<TcpLinkTestService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private TcpListener? _listener;
    private Socket? _workSocket;
    private int _running;
    private bool _disposed;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>阶段变化（后台线程触发）。</summary>
    public event Action<LinkTestPhase>? PhaseChanged;
    /// <summary>周期统计快照，约 250ms 一次（后台线程触发）。</summary>
    public event Action<LinkTestSnapshot>? SnapshotUpdated;
    /// <summary>一次测试结束（正常完成或被中止；后台线程触发）。</summary>
    public event Action<LinkTestSummary>? Completed;
    /// <summary>启动/链路错误，携带已本地化的消息（后台线程触发）。</summary>
    public event Action<string>? Failed;

    public TcpLinkTestService(ILogger<TcpLinkTestService> logger)
    {
        _logger = logger;
        _logger.LogInformation("TcpLinkTestService created");
    }

    /// <summary>
    /// 启动接收端（服务器）：监听绑定同步完成，失败直接抛出给调用方；
    /// 接收循环在后台运行，读到 EOF、链路断开或被停止时发布 <see cref="Completed"/>。
    /// </summary>
    public async Task StartReceiverAsync(string bindAddress, int port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                Failed?.Invoke(LocalizationRegistry.Get("LinkTest.Msg_Busy"));
                return;
            }

            var ip = IPAddress.TryParse(bindAddress, out var parsed) ? parsed : IPAddress.Any;
            var listener = new TcpListener(ip, port);
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _running, 0);
                _logger.LogError(ex, "TCP link test listen failed: {Address}:{Port}", ip, port);
                throw;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _listener = listener;
            _runTask = Task.Run(() => RunReceiverAsync(listener, cts.Token));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 启动发送端（客户端）：连接与发送全部在后台执行，
    /// 连接失败/超时通过 <see cref="Failed"/> 报告。
    /// </summary>
    public async Task StartSenderAsync(string targetAddress, int port, LinkTestOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                Failed?.Invoke(LocalizationRegistry.Get("LinkTest.Msg_Busy"));
                return;
            }

            var ip = IPAddress.TryParse(targetAddress, out var parsed) ? parsed : IPAddress.Loopback;
            var endpoint = new IPEndPoint(ip, port);
            var cts = new CancellationTokenSource();
            _cts = cts;
            _runTask = Task.Run(() => RunSenderAsync(endpoint, options, cts.Token));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>停止当前测试并等待后台任务退出（最多 2 秒）。可重复调用。</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _cts?.Cancel();
            SafeCloseListener();
            SafeCloseWorkSocket();
            var task = _runTask;
            if (task is not null)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger.LogDebug(ex, "TCP link test stop observed task fault"); }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>空数据的已中止汇总：保证停止指令总有 Completed 回执，界面不会停留在「测试中」。</summary>
    private static LinkTestSummary AbortedEmptySummary(bool isSender) => new()
    {
        Completed = false,
        IsSender = isSender,
        ExpectedPackets = -1,
        LostPackets = -1,
        LossRate = -1,
    };

    private async Task RunReceiverAsync(TcpListener listener, CancellationToken ct)
    {
        LinkTestSummary summary = AbortedEmptySummary(isSender: false);
        try
        {
            SetPhase(LinkTestPhase.WaitingPeer);

            // Pending 轮询 + 取消令牌：等待连接期间也能及时响应停止。
            TcpClient? client = null;
            while (!ct.IsCancellationRequested)
            {
                if (listener.Pending())
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    break;
                }
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            if (client is null || ct.IsCancellationRequested)
            {
                return; // 未开始传输即被停止：finally 发布空中止汇总（状态显示为已中止，非错误）
            }

            using (client)
            using (ct.Register(() => SafeCloseWorkSocket()))
            {
                var socket = client.Client;
                Volatile.Write(ref _workSocket, socket);
                SetPhase(LinkTestPhase.Transferring);

                var buffer = new byte[64 * 1024];
                var tracker = new LinkTestSnapshotTracker();
                bool eofReached = false;
                bool sawFinMarker = false;
                bool linkBroken = false;
                // 正常结束时发送端在流尾写 16 字节结束标记（复用链路测试协议头），
                // 用于区分「对端正常结束」与「对端中途停止」——两者在 TCP 上都表现为 EOF。
                var finMarker = new byte[LinkTestUdpProtocol.HeaderSize];
                LinkTestUdpProtocol.WriteHeader(finMarker, LinkTestUdpProtocol.FlagFin, 0);
                var tail = new byte[LinkTestUdpProtocol.HeaderSize];

                while (!ct.IsCancellationRequested)
                {
                    int n;
                    try
                    {
                        n = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    }
                    catch (SocketException ex) when (
                        ex.SocketErrorCode == SocketError.Interrupted ||
                        ex.SocketErrorCode == SocketError.ConnectionAborted ||
                        ex.SocketErrorCode == SocketError.ConnectionReset ||
                        ex.SocketErrorCode == SocketError.OperationAborted)
                    {
                        linkBroken = true;
                        break; // 对端中断或本端关闭：按已收数据汇总
                    }
                    catch (ObjectDisposedException)
                    {
                        break; // 停止测试时 Socket 被关闭
                    }

                    if (n == 0)
                    {
                        eofReached = true;
                        sawFinMarker = tail.AsSpan().SequenceEqual(finMarker);
                        break; // 流结束：有结束标记才算正常完成
                    }

                    tracker.Add(n, 0);
                    UpdateTail(tail, buffer, n);
                    if (tracker.DueForSnapshot())
                    {
                        SnapshotUpdated?.Invoke(tracker.Emit(LinkTestPhase.Transferring, -1));
                    }
                }

                if (sawFinMarker)
                {
                    tracker.Subtract(LinkTestUdpProtocol.HeaderSize); // 流尾 16 字节是结束标记，不计入数据量
                }
                summary = BuildSummary(tracker, isSender: false, completed: sawFinMarker && !ct.IsCancellationRequested);
                if (!tracker.HasData)
                {
                    Failed?.Invoke(linkBroken || eofReached
                        ? LocalizationRegistry.Get("LinkTest.Msg_NoData")
                        : LocalizationRegistry.Get("LinkTest.Msg_AbortedNoData"));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TCP link test receiver error");
            Failed?.Invoke(LocalizationRegistry.Get("LinkTest.Msg_ReceiveFail", ex.Message));
        }
        finally
        {
            SafeCloseListener();
            SafeCloseWorkSocket();
            Volatile.Write(ref _workSocket, null);
            Volatile.Write(ref _listener, null);
            _cts = null;
            _runTask = null;
            Interlocked.Exchange(ref _running, 0);
            Completed?.Invoke(summary); // 每次运行必有回执：无数据时为空中止汇总
        }
    }

    private async Task RunSenderAsync(IPEndPoint endpoint, LinkTestOptions options, CancellationToken ct)
    {
        LinkTestSummary summary = AbortedEmptySummary(isSender: true);
        try
        {
            SetPhase(LinkTestPhase.Connecting);

            using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using (ct.Register(() => SafeCloseWorkSocket()))
            {
                socket.SendBufferSize = 1024 * 1024;
                Volatile.Write(ref _workSocket, socket);

                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.ConnectTimeoutSec)));
                    await socket.ConnectAsync(endpoint, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Failed?.Invoke(LocalizationRegistry.Get("LinkTest.Msg_ConnectTimeout", options.ConnectTimeoutSec));
                    return;
                }
                catch (Exception ex)
                {
                    Failed?.Invoke(LocalizationRegistry.Get("LinkTest.Msg_ConnectFail", ex.Message));
                    return;
                }

                SetPhase(LinkTestPhase.Transferring);
                var buffer = new byte[Math.Clamp(options.ChunkSize, 1024, 4 * 1024 * 1024)];
                var tracker = new LinkTestSnapshotTracker();
                var sw = Stopwatch.StartNew();
                var durationTicks = (long)Math.Max(1, options.DurationSec) * (double)Stopwatch.Frequency;
                var pacing = new RatePacer(options.RateLimitBps);
                bool completedNormally = false;

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await socket.SendAsync(buffer, ct).ConfigureAwait(false);
                        tracker.Add(buffer.Length, 0);

                        if (sw.ElapsedTicks >= durationTicks) break;

                        if (tracker.DueForSnapshot())
                        {
                            SnapshotUpdated?.Invoke(tracker.Emit(LinkTestPhase.Transferring,
                                Math.Clamp(sw.ElapsedTicks / durationTicks, 0, 1)));
                        }

                        if (!await pacing.PaceAsync(tracker.TotalBytes, sw.ElapsedTicks, ct).ConfigureAwait(false))
                        {
                            break; // 限速等待中被取消：按已发数据出中止汇总
                        }
                    }

                    if (!ct.IsCancellationRequested)
                    {
                        SetPhase(LinkTestPhase.Finishing);
                        try
                        {
                            // 流尾结束标记：接收端据此把 EOF 判定为正常完成（而非对端中止）
                            var finMarker = new byte[LinkTestUdpProtocol.HeaderSize];
                            LinkTestUdpProtocol.WriteHeader(finMarker, LinkTestUdpProtocol.FlagFin, 0);
                            await socket.SendAsync(finMarker, ct).ConfigureAwait(false);
                            socket.Shutdown(SocketShutdown.Send);
                            // 等待对端关闭以确认送达（最多 5 秒；停止测试会关闭 Socket 中断 Poll）。
                            if (socket.Poll(5_000_000, SelectMode.SelectRead))
                            {
                                _ = socket.Receive(buffer);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "TCP link test shutdown handshake skipped");
                        }
                        completedNormally = true;
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }

                summary = BuildSummary(tracker, isSender: true, completed: completedNormally);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TCP link test sender error");
            Failed?.Invoke(LocalizationRegistry.Get("LinkTest.Msg_SendFail", ex.Message));
        }
        finally
        {
            SafeCloseWorkSocket();
            Volatile.Write(ref _workSocket, null);
            _cts = null;
            _runTask = null;
            Interlocked.Exchange(ref _running, 0);
            Completed?.Invoke(summary); // 每次运行必有回执：无数据时为空中止汇总
        }
    }

    /// <summary>维护接收流的滚动尾部窗口，供 EOF 时比对结束标记。</summary>
    private static void UpdateTail(byte[] tail, byte[] buffer, int n)
    {
        if (n >= tail.Length)
        {
            Buffer.BlockCopy(buffer, n - tail.Length, tail, 0, tail.Length);
        }
        else
        {
            Array.Copy(tail, n, tail, 0, tail.Length - n);
            Buffer.BlockCopy(buffer, 0, tail, tail.Length - n, n);
        }
    }

    private static LinkTestSummary BuildSummary(LinkTestSnapshotTracker tracker, bool isSender, bool completed) => new()
    {
        TotalBytes = tracker.TotalBytes,
        TotalPackets = tracker.TotalPackets,
        ExpectedPackets = -1,
        LostPackets = -1,
        LossRate = -1,
        ElapsedSec = tracker.ElapsedSec,
        AvgBps = tracker.AvgBps,
        Completed = completed,
        IsSender = isSender,
        HasRemoteStats = false,
    };

    private void SetPhase(LinkTestPhase phase) => PhaseChanged?.Invoke(phase);

    private void SafeCloseListener()
    {
        try { _listener?.Stop(); }
        catch (Exception ex) { _logger.LogDebug(ex, "TCP link test listener close error"); }
        _listener = null;
    }

    private void SafeCloseWorkSocket()
    {
        try { Volatile.Read(ref _workSocket)?.Close(); }
        catch (Exception ex) { _logger.LogDebug(ex, "TCP link test work socket close error"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "TcpLinkTestService dispose stop error"); }
        _gate.Dispose();
        _logger.LogInformation("TcpLinkTestService disposed");
    }
}

/// <summary>
/// 发送端限速器：按累计字节数与目标速率计算计划时刻，超前时睡眠等待。
/// 速率统计以实际时钟为准，长期均值收敛到目标速率；不限速（rateBps&lt;=0）时不等待。
/// </summary>
internal sealed class RatePacer
{
    private readonly double _rateBps;
    private const double MinSleepSec = 0.002; // 低于 2ms 的超前不等待，避免定时器粒度抖动

    public RatePacer(long rateBps)
    {
        _rateBps = rateBps;
    }

    /// <summary>
    /// 发送 totalBytes 后调用；超前于计划时刻则异步等待。
    /// 返回 false 表示等待期间被取消。
    /// </summary>
    public async Task<bool> PaceAsync(long totalBytes, long elapsedTicks, CancellationToken ct)
    {
        if (_rateBps <= 0) return true;
        var plannedTicks = (long)(totalBytes / _rateBps * Stopwatch.Frequency);
        var aheadTicks = plannedTicks - elapsedTicks;
        if (aheadTicks <= MinSleepSec * Stopwatch.Frequency) return true;
        var sleepMs = (int)Math.Ceiling(aheadTicks * 1000.0 / Stopwatch.Frequency);
        try
        {
            await Task.Delay(Math.Max(1, sleepMs), ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
