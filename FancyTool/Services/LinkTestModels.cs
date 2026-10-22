using System.Diagnostics;

namespace FancyToolAva.Services;

/// <summary>
/// 链路测试（带宽 / 丢包率）传输参数。
/// </summary>
public sealed class LinkTestOptions
{
    /// <summary>测试时长（秒），发送端到达后自动结束。</summary>
    public int DurationSec { get; init; } = 10;

    /// <summary>TCP 单次发送块大小（字节）或 UDP 单包大小（字节，含协议头）。</summary>
    public int ChunkSize { get; init; } = 64 * 1024;

    /// <summary>目标发送速率（字节/秒）；0 表示不限速。</summary>
    public long RateLimitBps { get; init; }

    /// <summary>TCP 客户端连接超时（秒）。</summary>
    public int ConnectTimeoutSec { get; init; } = 10;
}

/// <summary>
/// 链路测试阶段，用于界面状态提示。
/// </summary>
public enum LinkTestPhase
{
    /// <summary>等待对端连接 / 首包。</summary>
    WaitingPeer,

    /// <summary>TCP 客户端正在连接。</summary>
    Connecting,

    /// <summary>数据传输中。</summary>
    Transferring,

    /// <summary>发送完毕，等待对端回传统计（UDP）或等待对端关闭（TCP）。</summary>
    Finishing,
}

/// <summary>
/// 测试过程中的周期统计快照（后台线程发布，UI 侧需切换线程）。
/// </summary>
public sealed class LinkTestSnapshot
{
    public LinkTestPhase Phase { get; init; }
    /// <summary>累计字节数。</summary>
    public long TotalBytes { get; init; }
    /// <summary>累计包数（UDP；TCP 为块数近似，仅作展示）。</summary>
    public long TotalPackets { get; init; }
    /// <summary>距测试开始的秒数（接收端从首包/首连接起算）。</summary>
    public double ElapsedSec { get; init; }
    /// <summary>最近统计窗口内的速率（字节/秒）。</summary>
    public double CurrentBps { get; init; }
    /// <summary>自开始的平均速率（字节/秒）。</summary>
    public double AvgBps { get; init; }
    /// <summary>进度比例 0..1；接收端时长未知时为 -1。</summary>
    public double ProgressRatio { get; init; }
}

/// <summary>
/// 一次链路测试的最终结果。
/// </summary>
public sealed class LinkTestSummary
{
    /// <summary>本端累计传输字节数。</summary>
    public long TotalBytes { get; init; }
    /// <summary>本端累计包数（TCP 无包概念时为 0）。</summary>
    public long TotalPackets { get; init; }
    /// <summary>期望包数：UDP 接收端为 FIN 声明的发送总数；UDP 发送端为实际发送数；未知为 -1。</summary>
    public long ExpectedPackets { get; init; } = -1;
    /// <summary>对端统计的丢失包数；未知为 -1。</summary>
    public long LostPackets { get; init; } = -1;
    /// <summary>丢包率 0..1；未知为 -1。</summary>
    public double LossRate { get; init; } = -1;
    /// <summary>本端有效传输秒数。</summary>
    public double ElapsedSec { get; init; }
    /// <summary>本端平均速率（字节/秒）。</summary>
    public double AvgBps { get; init; }
    /// <summary>是否正常完成（未被用户中止、链路未中断）。</summary>
    public bool Completed { get; init; }
    /// <summary>本端是否为发送端。</summary>
    public bool IsSender { get; init; }
    /// <summary>UDP 发送端是否收到对端回传的丢包统计。</summary>
    public bool HasRemoteStats { get; init; }
}

/// <summary>
/// 发送/接收循环共用的累计统计器：维护累计字节与包数，
/// 按固定间隔生成本窗口速率 + 平均速率的快照。非线程安全，仅限单个循环线程使用。
/// </summary>
internal sealed class LinkTestSnapshotTracker
{
    private readonly double _snapshotIntervalSec;
    private long _firstTicks = -1;
    private long _lastEmitTicks;
    private long _totalBytes;
    private long _totalPackets;

    public LinkTestSnapshotTracker(double snapshotIntervalSec = 0.25)
    {
        _snapshotIntervalSec = snapshotIntervalSec;
    }

    public long TotalBytes => _totalBytes;
    public long TotalPackets => _totalPackets;
    public bool HasData => _totalBytes > 0;

    /// <summary>记录一笔收发；首个字节到达时刻作为计时起点。</summary>
    public void Add(long byteCount, long packetCount = 1)
    {
        if (_firstTicks < 0)
        {
            _firstTicks = Stopwatch.GetTimestamp();
            _lastEmitTicks = _firstTicks;
        }
        _totalBytes += byteCount;
        _totalPackets += packetCount;
    }

    /// <summary>从累计值中扣除协议字节（如 TCP 流尾结束标记）。</summary>
    public void Subtract(long byteCount)
    {
        _totalBytes -= byteCount;
    }

    /// <summary>自首个字节以来的有效传输秒数；尚无数据时为 0。</summary>
    public double ElapsedSec
    {
        get
        {
            if (_firstTicks < 0) return 0;
            return Math.Max(0, Stopwatch.GetTimestamp() - _firstTicks) / (double)Stopwatch.Frequency;
        }
    }

    /// <summary>距上次快照是否已超过上报间隔。</summary>
    public bool DueForSnapshot()
    {
        if (_firstTicks < 0) return false;
        return (Stopwatch.GetTimestamp() - _lastEmitTicks) / (double)Stopwatch.Frequency >= _snapshotIntervalSec;
    }

    /// <summary>生成快照并推进速率窗口起点。</summary>
    public LinkTestSnapshot Emit(LinkTestPhase phase, double progressRatio)
    {
        var now = Stopwatch.GetTimestamp();
        double windowSec = Math.Max(1e-6, (now - _lastEmitTicks) / (double)Stopwatch.Frequency);
        double currentBps = (_totalBytes - _windowStartBytes) / windowSec;
        double elapsedSec = Math.Max(0, now - _firstTicks) / (double)Stopwatch.Frequency;
        double avgBps = elapsedSec > 0 ? _totalBytes / elapsedSec : 0;
        _lastEmitTicks = now;
        _windowStartBytes = _totalBytes;
        return new LinkTestSnapshot
        {
            Phase = phase,
            TotalBytes = _totalBytes,
            TotalPackets = _totalPackets,
            ElapsedSec = elapsedSec,
            CurrentBps = currentBps,
            AvgBps = avgBps,
            ProgressRatio = progressRatio,
        };
    }

    /// <summary>累计平均速率（字节/秒）。</summary>
    public double AvgBps => ElapsedSec > 0 ? _totalBytes / ElapsedSec : 0;

    private long _windowStartBytes;
}
