using Lang.Avalonia;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace FancyToolAva.Services;

/// <summary>
/// UDP 链路带宽 / 丢包率测试：独立于 <see cref="UdpService"/> 使用专属 Socket。
/// 发送端按序号发送 DATA 包，结束时发送 FIN（声明总数）并等待对端 FIN-ACK；
/// 接收端按魔数过滤流量、按序号统计收包，收到 FIN 后再收一小段尾巴包，
/// 将「接收包数 / 字节 / 最大序号」回传给发送端，两端都能看到丢包率。
/// </summary>
public sealed class UdpLinkTestService : IDisposable
{
    private readonly ILogger<UdpLinkTestService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private Socket? _socket;
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

    public UdpLinkTestService(ILogger<UdpLinkTestService> logger)
    {
        _logger = logger;
        _logger.LogInformation("UdpLinkTestService created");
    }

    /// <summary>
    /// 启动接收端：绑定同步完成，失败直接抛出给调用方；
    /// 收包循环在后台运行，收到 FIN、静默超时或被停止时发布 <see cref="Completed"/>。
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
            var socket = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.ReceiveBufferSize = LinkTestUdpProtocol.ReceiveBufferBytes;
                socket.Bind(new IPEndPoint(ip, port));
            }
            catch (Exception ex)
            {
                socket.Dispose();
                Interlocked.Exchange(ref _running, 0);
                _logger.LogError(ex, "UDP link test bind failed: {Address}:{Port}", ip, port);
                throw;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            Volatile.Write(ref _socket, socket);
            _runTask = Task.Run(() => RunReceiverAsync(socket, cts.Token));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 启动发送端：发送与 FIN-ACK 等待全部在后台执行，
    /// 目标不可达等错误通过 <see cref="Failed"/> 报告。
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
            SafeCloseSocket();
            var task = _runTask;
            if (task is not null)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger.LogDebug(ex, "UDP link test stop observed task fault"); }
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

    private async Task RunReceiverAsync(Socket socket, CancellationToken ct)
    {
        LinkTestSummary summary = AbortedEmptySummary(isSender: false);
        try
        {
            using (socket)
            using (ct.Register(() => SafeCloseSocket()))
            {
                SetPhase(LinkTestPhase.WaitingPeer);
                socket.ReceiveTimeout = LinkTestUdpProtocol.ReceiveTimeoutMs;

                var buffer = new byte[65_535];
                EndPoint anyEp = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
                var tracker = new LinkTestSnapshotTracker();
                long maxSeq = -1;
                long declaredTotal = -1;          // FIN 声明的发送总数
                EndPoint? senderEp = null;
                long lastPacketTicks = Stopwatch.GetTimestamp();
                long startedTicks = lastPacketTicks;
                long finDeadlineTicks = 0;

                while (!ct.IsCancellationRequested)
                {
                    int n;
                    try
                    {
                        EndPoint remote = anyEp; // ReceiveFrom 需要可写实例
                        n = socket.ReceiveFrom(buffer, ref remote);
                        lastPacketTicks = Stopwatch.GetTimestamp();
                        if (senderEp is null && n >= LinkTestUdpProtocol.HeaderSize)
                        {
                            senderEp = remote; // 记住对端，用于回传 FIN-ACK
                        }
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        var idleMs = (Stopwatch.GetTimestamp() - lastPacketTicks) * 1000.0 / Stopwatch.Frequency;
                        if (declaredTotal >= 0)
                        {
                            break; // FIN 尾巴时间已到（超时即可结束）
                        }
                        if (tracker.HasData && idleMs >= LinkTestUdpProtocol.IdleTimeoutMs)
                        {
                            break; // 有数据但等不到 FIN：静默超时结束
                        }
                        if (!tracker.HasData &&
                            (Stopwatch.GetTimestamp() - startedTicks) * 1000.0 / Stopwatch.Frequency >= LinkTestUdpProtocol.FirstPacketTimeoutMs)
                        {
                            break; // 等不到首包
                        }
                        if (tracker.HasData && tracker.DueForSnapshot())
                        {
                            SnapshotUpdated?.Invoke(tracker.Emit(LinkTestPhase.Transferring, -1));
                        }
                        continue;
                    }
                    catch (SocketException ex) when (
                        ex.SocketErrorCode == SocketError.ConnectionReset ||
                        ex.SocketErrorCode == SocketError.Interrupted ||
                        ex.SocketErrorCode == SocketError.OperationAborted)
                    {
                        // Windows 上未连接 UDP socket 可能报 ConnectionReset（ICMP 端口不可达），忽略继续收
                        continue;
                    }
                    catch (ObjectDisposedException)
                    {
                        break; // 停止测试时 Socket 被关闭
                    }

                    if (n < LinkTestUdpProtocol.HeaderSize ||
                        !LinkTestUdpProtocol.TryReadHeader(buffer, out var flags, out var seq))
                    {
                        continue; // 非链路测试流量（魔数不符）：忽略，不影响与普通 UDP 调试共存
                    }

                    if (flags == LinkTestUdpProtocol.FlagFin)
                    {
                        declaredTotal = seq;
                        finDeadlineTicks = Stopwatch.GetTimestamp() +
                            (long)(LinkTestUdpProtocol.FinTailMs * (Stopwatch.Frequency / 1000.0));
                        socket.ReceiveTimeout = Math.Min(LinkTestUdpProtocol.FinTailMs, LinkTestUdpProtocol.ReceiveTimeoutMs);
                        continue;
                    }
                    if (flags == LinkTestUdpProtocol.FlagFinAck)
                    {
                        continue; // 接收端不应收到 FIN-ACK，忽略
                    }

                    // DATA
                    if (declaredTotal >= 0 && Stopwatch.GetTimestamp() > finDeadlineTicks)
                    {
                        continue; // FIN 之后的迟到包不重复计数
                    }
                    tracker.Add(n, 1);
                    if (seq > maxSeq) maxSeq = seq;
                    if (tracker.DueForSnapshot())
                    {
                        SnapshotUpdated?.Invoke(tracker.Emit(LinkTestPhase.Transferring, -1));
                    }
                }

                long expected = declaredTotal >= 0
                    ? declaredTotal
                    : (maxSeq >= 0 ? maxSeq + 1 : 0);
                long lost = Math.Max(0, expected - tracker.TotalPackets);
                double lossRate = expected > 0 ? (double)lost / expected : -1;

                // 仅在收到 FIN（对端声明了总数）后回传统计：中止/静默超时结束时对端
                // 还在发送，此刻的统计是过期快照，发送端若消费会得到错误的丢包率。
                if (senderEp is not null && declaredTotal >= 0)
                {
                    TrySendFinAck(socket, senderEp, tracker.TotalPackets, tracker.TotalBytes, maxSeq);
                }

                summary = new LinkTestSummary
                {
                    TotalBytes = tracker.TotalBytes,
                    TotalPackets = tracker.TotalPackets,
                    ExpectedPackets = expected,
                    LostPackets = expected > 0 ? lost : -1,
                    LossRate = lossRate,
                    ElapsedSec = tracker.ElapsedSec,
                    AvgBps = tracker.AvgBps,
                    Completed = declaredTotal >= 0 && !ct.IsCancellationRequested,
                    IsSender = false,
                    HasRemoteStats = false,
                };

                if (!tracker.HasData)
                {
                    Failed?.Invoke(LocalizationRegistry.Get("LinkTest.Msg_NoData"));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UDP link test receiver error");
            Failed?.Invoke(LocalizationRegistry.Get("LinkTest.Msg_ReceiveFail", ex.Message));
        }
        finally
        {
            SafeCloseSocket();
            Volatile.Write(ref _socket, null);
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
            var packetSize = Math.Clamp(options.ChunkSize, LinkTestUdpProtocol.MinPacketSize, LinkTestUdpProtocol.MaxPacketSize);
            using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            using (ct.Register(() => SafeCloseSocket()))
            {
                socket.SendBufferSize = LinkTestUdpProtocol.ReceiveBufferBytes;
                socket.Connect(endpoint);
                Volatile.Write(ref _socket, socket);
                SetPhase(LinkTestPhase.Transferring);

                var packet = new byte[packetSize];
                for (int i = LinkTestUdpProtocol.HeaderSize; i < packet.Length; i++)
                {
                    packet[i] = (byte)i; // 填充模式，便于抓包对账
                }

                var tracker = new LinkTestSnapshotTracker();
                var sw = Stopwatch.StartNew();
                var durationTicks = (long)Math.Max(1, options.DurationSec) * (double)Stopwatch.Frequency;
                var pacing = new RatePacer(options.RateLimitBps);
                long seq = 0;
                bool completedNormally = false;
                bool hasRemote = false;
                long remotePackets = 0;

                try
                {
                    while (!ct.IsCancellationRequested && sw.ElapsedTicks < durationTicks)
                    {
                        LinkTestUdpProtocol.WriteHeader(packet, LinkTestUdpProtocol.FlagData, seq);
                        await socket.SendAsync(packet, ct).ConfigureAwait(false);
                        tracker.Add(packet.Length, 1);
                        seq++;

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
                        // 发送 FIN（声明发送总数）
                        SetPhase(LinkTestPhase.Finishing);
                        var fin = new byte[LinkTestUdpProtocol.HeaderSize];
                        LinkTestUdpProtocol.WriteHeader(fin, LinkTestUdpProtocol.FlagFin, seq);
                        await socket.SendAsync(fin, ct).ConfigureAwait(false);

                        // 等待对端 FIN-ACK（对端重复发送多个，取第一个即可）
                        long remoteBytes = 0, remoteMaxSeq = 0;
                        var ackBuffer = new byte[LinkTestUdpProtocol.FinAckPacketSize];
                        var deadline = Stopwatch.GetTimestamp() +
                            (long)(LinkTestUdpProtocol.FinAckTimeoutMs * (Stopwatch.Frequency / 1000.0));
                        try
                        {
                            socket.ReceiveTimeout = LinkTestUdpProtocol.FinAckTimeoutMs;
                            while (Stopwatch.GetTimestamp() < deadline && !ct.IsCancellationRequested)
                            {
                                int n = socket.Receive(ackBuffer);
                                if (LinkTestUdpProtocol.TryReadFinAck(ackBuffer.AsSpan(0, n), out remotePackets, out remoteBytes, out remoteMaxSeq))
                                {
                                    hasRemote = true;
                                    break;
                                }
                            }
                        }
                        catch (SocketException ex) when (
                            ex.SocketErrorCode == SocketError.TimedOut ||
                            ex.SocketErrorCode == SocketError.ConnectionReset ||
                            ex.SocketErrorCode == SocketError.Interrupted ||
                            ex.SocketErrorCode == SocketError.OperationAborted)
                        {
                            // 等待超时或 ICMP 端口不可达：按无对端统计处理
                        }
                        catch (ObjectDisposedException) { }

                        completedNormally = true;
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }

                long sentPackets = seq;
                long lostPackets = -1;
                double lossRate = -1;
                if (hasRemote)
                {
                    lostPackets = Math.Max(0, sentPackets - remotePackets);
                    lossRate = sentPackets > 0 ? (double)lostPackets / sentPackets : -1;
                }

                summary = new LinkTestSummary
                {
                    TotalBytes = tracker.TotalBytes,
                    TotalPackets = sentPackets,
                    ExpectedPackets = sentPackets,
                    LostPackets = lostPackets,
                    LossRate = lossRate,
                    ElapsedSec = tracker.ElapsedSec,
                    AvgBps = tracker.AvgBps,
                    Completed = completedNormally,
                    IsSender = true,
                    HasRemoteStats = hasRemote,
                };
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UDP link test sender error");
            Failed?.Invoke(LocalizationRegistry.Get("LinkTest.Msg_SendFail", ex.Message));
        }
        finally
        {
            SafeCloseSocket();
            Volatile.Write(ref _socket, null);
            _cts = null;
            _runTask = null;
            Interlocked.Exchange(ref _running, 0);
            Completed?.Invoke(summary); // 每次运行必有回执：无数据时为空中止汇总
        }
    }

    private static void TrySendFinAck(Socket socket, EndPoint target, long receivedPackets, long receivedBytes, long maxSeq)
    {
        var ack = new byte[LinkTestUdpProtocol.FinAckPacketSize];
        LinkTestUdpProtocol.WriteFinAck(ack, receivedPackets, receivedBytes, maxSeq);
        for (int i = 0; i < LinkTestUdpProtocol.FinAckRepeatCount; i++)
        {
            try
            {
                socket.SendTo(ack, target);
            }
            catch (Exception)
            {
                // 回传失败不影响本端汇总
            }
            if (i < LinkTestUdpProtocol.FinAckRepeatCount - 1)
            {
                Thread.Sleep(LinkTestUdpProtocol.FinAckRepeatIntervalMs);
            }
        }
    }

    private void SetPhase(LinkTestPhase phase) => PhaseChanged?.Invoke(phase);

    private void SafeCloseSocket()
    {
        try { Volatile.Read(ref _socket)?.Close(); }
        catch (Exception ex) { _logger.LogDebug(ex, "UDP link test socket close error"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "UdpLinkTestService dispose stop error"); }
        _gate.Dispose();
        _logger.LogInformation("UdpLinkTestService disposed");
    }
}
