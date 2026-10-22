// 链路测试烟测：在进程内通过环回地址驱动真实的 TcpLinkTestService / UdpLinkTestService，
// 覆盖 TCP 带宽收发、UDP 带宽+丢包率（含 FIN/FIN-ACK 统计回传）与发送端中止路径。
// 用法：dotnet run --project FancyTool/_tools/LinkTestSmokeTest [--out <目录>]

using System.Net;
using System.Net.Sockets;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;

var pass = 0;
var failures = new List<string>();

void Check(string name, bool ok, string detail)
{
    if (ok) { pass++; Console.WriteLine($"[PASS] {name}"); }
    else { failures.Add($"{name}: {detail}"); Console.WriteLine($"[FAIL] {name}: {detail}"); }
}

static int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    int p = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}

static async Task<LinkTestSummary?> WaitSummary(Task<LinkTestSummary> tcs, int timeoutSec)
{
    try { return await tcs.WaitAsync(TimeSpan.FromSeconds(timeoutSec)); }
    catch (TimeoutException) { return null; }
}

// ============ 场景 1：TCP 环回带宽（正常完成） ============
{
    var rx = new TcpLinkTestService(NullLogger<TcpLinkTestService>.Instance);
    var tx = new TcpLinkTestService(NullLogger<TcpLinkTestService>.Instance);
    var port = FreePort();
    var rxDone = new TaskCompletionSource<LinkTestSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
    var txDone = new TaskCompletionSource<LinkTestSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
    var errors = new List<string>();
    int rxSnapshots = 0, txSnapshots = 0;
    rx.Completed += s => rxDone.TrySetResult(s);
    tx.Completed += s => txDone.TrySetResult(s);
    rx.Failed += m => errors.Add("rx: " + m);
    tx.Failed += m => errors.Add("tx: " + m);
    rx.SnapshotUpdated += _ => Interlocked.Increment(ref rxSnapshots);
    tx.SnapshotUpdated += _ => Interlocked.Increment(ref txSnapshots);

    await rx.StartReceiverAsync("127.0.0.1", port);
    await tx.StartSenderAsync("127.0.0.1", port, new LinkTestOptions
    {
        DurationSec = 2,
        ChunkSize = 64 * 1024,
        ConnectTimeoutSec = 5,
    });

    var txSummary = await WaitSummary(txDone.Task, 20);
    var rxSummary = await WaitSummary(rxDone.Task, 20);
    await tx.StopAsync();
    await rx.StopAsync();
    tx.Dispose();
    rx.Dispose();

    Check("Tcp.ErrorsEmpty", errors.Count == 0, string.Join("; ", errors));
    Check("Tcp.BothCompleted", txSummary?.Completed == true && rxSummary?.Completed == true,
        $"tx={txSummary?.Completed} rx={rxSummary?.Completed}");
    Check("Tcp.BytesMatch", txSummary?.TotalBytes > 0 && txSummary!.TotalBytes == rxSummary!.TotalBytes,
        $"tx={txSummary?.TotalBytes} rx={rxSummary?.TotalBytes}");
    Check("Tcp.AvgBpsPositive", rxSummary!.AvgBps > 0, $"avg={rxSummary.AvgBps:F0} B/s");
    Check("Tcp.SnapshotsEmitted", rxSnapshots > 0 && txSnapshots > 0, $"rx={rxSnapshots} tx={txSnapshots}");
    Console.WriteLine($"      TCP 环回: {rxSummary.TotalBytes:N0} B / {rxSummary.ElapsedSec:0.00}s / {rxSummary.AvgBps * 8 / 1e6:0.00} Mbps");
}

// ============ 场景 2：UDP 环回带宽 + 丢包率（10 Mbps，正常完成） ============
{
    var rx = new UdpLinkTestService(NullLogger<UdpLinkTestService>.Instance);
    var tx = new UdpLinkTestService(NullLogger<UdpLinkTestService>.Instance);
    var port = FreePort();
    var rxDone = new TaskCompletionSource<LinkTestSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
    var txDone = new TaskCompletionSource<LinkTestSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
    var errors = new List<string>();
    rx.Completed += s => rxDone.TrySetResult(s);
    tx.Completed += s => txDone.TrySetResult(s);
    rx.Failed += m => errors.Add("rx: " + m);
    tx.Failed += m => errors.Add("tx: " + m);

    await rx.StartReceiverAsync("127.0.0.1", port);
    await tx.StartSenderAsync("127.0.0.1", port, new LinkTestOptions
    {
        DurationSec = 3,
        ChunkSize = 1400,
        RateLimitBps = 10_000_000 / 8, // 10 Mbps
    });

    var txSummary = await WaitSummary(txDone.Task, 30);
    var rxSummary = await WaitSummary(rxDone.Task, 30);
    await tx.StopAsync();
    await rx.StopAsync();
    tx.Dispose();
    rx.Dispose();

    Check("Udp.ErrorsEmpty", errors.Count == 0, string.Join("; ", errors));
    Check("Udp.BothCompleted", txSummary?.Completed == true && rxSummary?.Completed == true,
        $"tx={txSummary?.Completed} rx={rxSummary?.Completed}");
    Check("Udp.SenderHasRemoteStats", txSummary?.HasRemoteStats == true, "sender did not get FIN-ACK");
    Check("Udp.ExpectedPacketsMatch", rxSummary?.ExpectedPackets == txSummary?.TotalPackets,
        $"rx.expected={rxSummary?.ExpectedPackets} tx.sent={txSummary?.TotalPackets}");
    Check("Udp.LossAgrees", txSummary?.LostPackets >= 0 && txSummary!.LostPackets == rxSummary!.LostPackets,
        $"tx.lost={txSummary?.LostPackets} rx.lost={rxSummary?.LostPackets}");
    double lossRate = rxSummary!.LossRate >= 0 ? rxSummary.LossRate : 1;
    Check("Udp.LoopbackLossLow", lossRate < 0.01, $"loss={lossRate:P3}");
    Check("Udp.RateNearTarget", txSummary!.AvgBps > 0.8 * (10_000_000 / 8.0) && txSummary.AvgBps < 1.5 * (10_000_000 / 8.0),
        $"avg={txSummary.AvgBps * 8 / 1e6:0.00} Mbps (target 10)");
    Console.WriteLine($"      UDP 环回: 发 {txSummary.TotalPackets:N0} 包, 收 {rxSummary.TotalPackets:N0}, 丢 {rxSummary.LostPackets} ({lossRate:P3}), {txSummary.AvgBps * 8 / 1e6:0.00} Mbps");
}

// ============ 场景 3：TCP 发送端中止（进行中停止 → 已中止汇总） ============
{
    var rx = new TcpLinkTestService(NullLogger<TcpLinkTestService>.Instance);
    var tx = new TcpLinkTestService(NullLogger<TcpLinkTestService>.Instance);
    var port = FreePort();
    var txDone = new TaskCompletionSource<LinkTestSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
    var rxDone = new TaskCompletionSource<LinkTestSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
    tx.Completed += s => txDone.TrySetResult(s);
    rx.Completed += s => rxDone.TrySetResult(s);
    await rx.StartReceiverAsync("127.0.0.1", port);
    await tx.StartSenderAsync("127.0.0.1", port, new LinkTestOptions { DurationSec = 60, ChunkSize = 64 * 1024 });
    await Task.Delay(1200); // 发送一部分后停止
    await tx.StopAsync();

    var txSummary = await WaitSummary(txDone.Task, 10);
    var rxSummary = await WaitSummary(rxDone.Task, 10);
    await rx.StopAsync();
    tx.Dispose();
    rx.Dispose();

    Check("TcpAbort.SenderAborted", txSummary is { Completed: false, TotalBytes: > 0 },
        $"completed={txSummary?.Completed} bytes={txSummary?.TotalBytes}");
    Check("TcpAbort.ReceiverAborted", rxSummary is { Completed: false, TotalBytes: > 0 },
        $"completed={rxSummary?.Completed} bytes={rxSummary?.TotalBytes}");
    Console.WriteLine($"      中止: 发 {txSummary!.TotalBytes:N0} B / 收 {rxSummary!.TotalBytes:N0} B");
}

// ============ 场景 4：UDP 接收端先停（发送端拿不到 FIN-ACK，不崩） ============
{
    var rx = new UdpLinkTestService(NullLogger<UdpLinkTestService>.Instance);
    var tx = new UdpLinkTestService(NullLogger<UdpLinkTestService>.Instance);
    var port = FreePort();
    var txDone = new TaskCompletionSource<LinkTestSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
    var failed = new List<string>();
    tx.Completed += s => txDone.TrySetResult(s);
    tx.Failed += m => failed.Add(m);
    await rx.StartReceiverAsync("127.0.0.1", port);
    await tx.StartSenderAsync("127.0.0.1", port, new LinkTestOptions
    {
        DurationSec = 2,
        ChunkSize = 1200,
        RateLimitBps = 5_000_000 / 8,
    });
    await Task.Delay(300);
    await rx.StopAsync(); // 接收端先退出，发送端等不到 FIN-ACK
    rx.Dispose();

    var txSummary = await WaitSummary(txDone.Task, 15);
    await tx.StopAsync();
    tx.Dispose();

    Check("UdpPeerGone.SenderStillCompletes", txSummary?.Completed == true, $"completed={txSummary?.Completed} failed=[{string.Join(";", failed)}]");
    Check("UdpPeerGone.NoRemoteStats", txSummary?.HasRemoteStats == false, "HasRemoteStats should be false");
}

// ============ 场景 5：无数据时停止（等待对端期间停止 → 必有空中止回执） ============
{
    var rx = new TcpLinkTestService(NullLogger<TcpLinkTestService>.Instance);
    var port = FreePort();
    var rxDone = new TaskCompletionSource<LinkTestSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
    rx.Completed += s => rxDone.TrySetResult(s);
    await rx.StartReceiverAsync("127.0.0.1", port);
    await Task.Delay(200);
    await rx.StopAsync(); // 尚无发送端连接，直接停止
    var rxSummary = await WaitSummary(rxDone.Task, 10);
    rx.Dispose();

    Check("EarlyStop.EmptyAbortedSummary", rxSummary is { Completed: false, TotalBytes: 0 },
        $"completed={rxSummary?.Completed} bytes={rxSummary?.TotalBytes}");
}

Console.WriteLine();
Console.WriteLine($"合计: {pass} 通过, {failures.Count} 失败");
if (failures.Count > 0)
{
    foreach (var f in failures) Console.WriteLine("  " + f);
    return 1;
}
return 0;
