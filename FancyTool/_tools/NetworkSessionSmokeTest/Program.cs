// 网络会话烟测（TCP / UDP 多会话 P1）：在进程内用环回地址驱动真实的 SessionHub / TcpServerService / UdpService /
// BindEndpoint，覆盖端点规则、同端口冲突、同一 Hub 内环回互发、踢出客户端的事件、回收后端口立即可用、Hub 释放。
// 用法：dotnet run --project FancyTool/_tools/NetworkSessionSmokeTest

using System.Net;
using System.Net.Sockets;
using System.Text;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;

var pass = 0;
var failures = new List<string>();

void Check(string name, bool ok, string detail)
{
    if (ok) { pass++; Console.WriteLine($"[PASS] {name}"); }
    else { failures.Add($"{name}: {detail}"); Console.WriteLine($"[FAIL] {name}: {detail}"); }
}

static int FreeTcpPort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    int p = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}

static int FreeUdpPort()
{
    using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
}

static TaskCompletionSource<byte[]> RxOf(Action<Action<byte[]>> subscribe)
{
    var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    subscribe(b => tcs.TrySetResult(b));
    return tcs;
}

static async Task<string?> WaitText(Task<byte[]> task, int timeoutSec)
{
    try { return Encoding.UTF8.GetString(await task.WaitAsync(TimeSpan.FromSeconds(timeoutSec))); }
    catch (TimeoutException) { return null; }
}

static ISessionHub<ITcpServerService> NewTcpHub() => new SessionHub<ITcpServerService>(
    () => new TcpServerService(NullLogger<TcpServerService>.Instance),
    NullLogger<SessionHub<ITcpServerService>>.Instance);

static ISessionHub<IUdpService> NewUdpHub() => new SessionHub<IUdpService>(
    () => new UdpService(NullLogger<UdpService>.Instance),
    NullLogger<SessionHub<IUdpService>>.Instance);

// ============ 场景 1：绑定端点解析与冲突规则（纯函数） ============
Console.WriteLine("== 1. 绑定端点解析与冲突规则 ==");
{
    BindEndpoint W(string address, int port) => BindEndpoint.ForTcp(address, port);

    Check("Bind.TcpFallbackIsLoopback", BindEndpoint.ForTcp("not-an-ip", 80).Address.Equals(IPAddress.Loopback), BindEndpoint.ForTcp("not-an-ip", 80).ToString());
    Check("Bind.TcpBlankFallsBack", BindEndpoint.ForTcp(null, 80).Address.Equals(IPAddress.Loopback), BindEndpoint.ForTcp(null, 80).ToString());
    Check("Bind.UdpFallbackIsAny", BindEndpoint.ForUdp("", 80).Address.Equals(IPAddress.Any), BindEndpoint.ForUdp("", 80).ToString());
    Check("Bind.ParseKeepsAddressAndPort", BindEndpoint.ForTcp("192.0.2.10", 9100).ToString() == "192.0.2.10:9100", BindEndpoint.ForTcp("192.0.2.10", 9100).ToString());

    Check("Overlap.SameEndpoint", W("127.0.0.1", 9000).Overlaps(W("127.0.0.1", 9000)), "相同端点应互斥");
    Check("Overlap.OtherPort", !W("127.0.0.1", 9000).Overlaps(W("127.0.0.1", 9001)), "端口不同不应互斥");
    Check("Overlap.SpecificAddresses", !W("127.0.0.1", 9000).Overlaps(W("192.0.2.10", 9000)), "不同具体地址不应互斥");
    Check("Overlap.WildcardCoversSpecific", W("0.0.0.0", 9000).Overlaps(W("127.0.0.1", 9000)), "IPv4 通配应覆盖具体地址");
    Check("Overlap.SpecificCoveredByWildcard", W("127.0.0.1", 9000).Overlaps(W("0.0.0.0", 9000)), "具体地址应被 IPv4 通配覆盖");
    Check("Overlap.AnyVersusAny", W("0.0.0.0", 9000).Overlaps(W("0.0.0.0", 9000)), "两个通配应互斥");
    Check("Overlap.Ipv6AnyCoversIpv4", W("::", 9000).Overlaps(W("127.0.0.1", 9000)), "双栈下 [::] 应覆盖 IPv4");
    Check("Overlap.Ipv4WildcardIgnoresIpv6", !W("0.0.0.0", 9000).Overlaps(W("::1", 9000)), "IPv4 通配不应覆盖 IPv6 地址");
    Check("Overlap.MappedIpv4EqualsIpv4", W("::ffff:127.0.0.1", 9000).Overlaps(W("127.0.0.1", 9000)), "IPv4 映射地址应按 IPv4 比较");

    Check("TcpRule.RejectsOverlappingServer", EndpointConflict.Tcp(W("127.0.0.1", 9000), [W("0.0.0.0", 9000)]), "与运行中的服务端重叠应判冲突");
    Check("TcpRule.AllowsOtherPort", !EndpointConflict.Tcp(W("127.0.0.1", 9000), [W("0.0.0.0", 9001)]), "端口不同应放行");
    Check("TcpRule.AllowsWhenNoServers", !EndpointConflict.Tcp(W("127.0.0.1", 9000), []), "没有运行中的服务端应放行");

    var ep = BindEndpoint.ForUdp("0.0.0.0", 9100);
    Check("UdpRule.NormalVersusNormal", EndpointConflict.Udp(ep, false, [(ep, false)]), "普通对普通应判冲突");
    Check("UdpRule.NormalVersusMulticast", EndpointConflict.Udp(ep, false, [(ep, true)]), "普通对组播应判冲突");
    Check("UdpRule.MulticastVersusNormal", EndpointConflict.Udp(ep, true, [(ep, false)]), "组播对普通应判冲突");
    Check("UdpRule.MulticastVersusMulticast", !EndpointConflict.Udp(ep, true, [(ep, true)]), "组播之间应放行");
    Check("UdpRule.MulticastOtherPort", !EndpointConflict.Udp(ep, true, [(BindEndpoint.ForUdp("0.0.0.0", 9101), false)]), "端口不同应放行");
}

// ============ 场景 2：TCP 两个服务端会话同端口 ============
Console.WriteLine("== 2. TCP：两个服务端会话同端口 ==");
ISessionHub<ITcpServerService> tcpHub = NewTcpHub();
ITcpServerService server;
ITcpServerService client;
ITcpServerService secondClient;
string? clientEndpoint = null;
int serverPort;
{
    var first = tcpHub.CreateSession();
    var second = tcpHub.CreateSession();
    serverPort = FreeTcpPort();
    var candidate = BindEndpoint.ForTcp("127.0.0.1", serverPort);
    Check("TcpHub.CountTracksCreate", tcpHub.Count == 2, $"count={tcpHub.Count}");

    await first.StartServerAsync("127.0.0.1", serverPort, CancellationToken.None);
    Check("Tcp.RuleRejectsSecondServer", EndpointConflict.Tcp(candidate, [candidate]), "同端点的服务端会话应判为冲突");

    bool rejected = false;
    string error = "";
    try { await second.StartServerAsync("127.0.0.1", serverPort, CancellationToken.None); }
    catch (Exception ex) { rejected = true; error = $"{ex.GetType().Name}: {ex.Message}"; }
    Check("Tcp.SystemBindRejectsSecondServer", rejected, error);
    Check("Tcp.SecondServerNotRunning", !second.IsRunning, $"isRunning={second.IsRunning}");
    Check("Tcp.FirstServerStillRunning", first.IsRunning, $"isRunning={first.IsRunning}");

    tcpHub.ReleaseSession(second);
    Check("TcpHub.CountTracksRelease", tcpHub.Count == 1, $"count={tcpHub.Count}");
    server = first;
    client = tcpHub.CreateSession();
}

// ============ 场景 3：同一 Hub 内服务端与客户端环回互发 ============
Console.WriteLine("== 3. TCP：同一 Hub 内服务端与客户端环回互发 ==");
{
    var firstClient = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var serverEvents = new List<ConnectionEventType>();
    server.ConnectionStateChanged += (_, e) =>
    {
        lock (serverEvents) serverEvents.Add(e.Type);
        if (e.Type == ConnectionEventType.ClientConnected)
        {
            clientEndpoint = e.Endpoint;
            firstClient.TrySetResult();
        }
    };
    var serverRx = RxOf(h => server.BytesReceived += h);
    var clientRx = RxOf(h => client.BytesReceived += h);

    await client.ConnectClientAsync("127.0.0.1", serverPort, CancellationToken.None);
    await firstClient.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check("Tcp.ServerSeesClientEndpoint", !string.IsNullOrEmpty(clientEndpoint), $"endpoint=[{clientEndpoint}]");

    await client.SendAsync("hello", false);
    string? toServer = await WaitText(serverRx.Task, 5);
    Check("Tcp.ClientToServer", toServer == "hello", $"received=[{toServer}]");

    await server.SendAsync("hi", false, clientEndpoint);
    string? toClient = await WaitText(clientRx.Task, 5);
    Check("Tcp.ServerToSelectedClient", toClient == "hi", $"received=[{toClient}]");

    // ============ 场景 4：踢出客户端 ============
    Console.WriteLine("== 4. TCP：踢出客户端 ==");
    lock (serverEvents) serverEvents.Clear();
    server.DisconnectClient(clientEndpoint!);
    await Task.Delay(600);
    ConnectionEventType[] events;
    lock (serverEvents) events = [.. serverEvents];
    string joined = string.Join(",", events);
    Check("Tcp.KickRaisesClientDisconnected", events.Contains(ConnectionEventType.ClientDisconnected), joined);
    Check("Tcp.KickKeepsListener", server.IsRunning && !events.Contains(ConnectionEventType.Disconnected), $"running={server.IsRunning} events={joined}");

    secondClient = tcpHub.CreateSession();
    await secondClient.ConnectClientAsync("127.0.0.1", serverPort, CancellationToken.None);
    // 客户端模式下 IsRunning 就是"已连接"（接口没有单独的 IsConnected）。
    Check("Tcp.AcceptsNewClientAfterKick", secondClient.IsRunning, $"running={secondClient.IsRunning}");
}

// ============ 场景 5：回收与释放 ============
Console.WriteLine("== 5. 回收与释放 ==");
{
    int releasedPort = serverPort;
    tcpHub.ReleaseSession(server);
    Check("Tcp.ReleasedServerStopped", !server.IsRunning, $"isRunning={server.IsRunning}");

    bool reusable = false;
    string bindError = "";
    try { var probe = new TcpListener(IPAddress.Loopback, releasedPort); probe.Start(); reusable = true; probe.Stop(); }
    catch (Exception ex) { bindError = ex.Message; }
    Check("Tcp.PortReusableAfterRelease", reusable, bindError);
    Check("TcpHub.CountWithTwoClients", tcpHub.Count == 2, $"count={tcpHub.Count}");

    tcpHub.Dispose();
    tcpHub.Dispose();
    Check("TcpHub.DisposeIdempotent", true, "");
    Check("TcpHub.DisposeStopsRemaining", !client.IsRunning && !secondClient.IsRunning, $"client={client.IsRunning} second={secondClient.IsRunning}");

    bool createThrows = false;
    try { tcpHub.CreateSession(); }
    catch (ObjectDisposedException) { createThrows = true; }
    Check("TcpHub.CreateAfterDisposeThrows", createThrows, "");
    tcpHub.ReleaseSession(client);
    Check("TcpHub.ReleaseAfterDisposeOk", true, "");
}

// ============ 场景 6：UDP 两个普通会话同端口 ============
Console.WriteLine("== 6. UDP：两个普通会话同端口 ==");
ISessionHub<IUdpService> udpHub = NewUdpHub();
IUdpService normal;
{
    int udpPort = FreeUdpPort();
    var a = udpHub.CreateSession();
    var b = udpHub.CreateSession();
    var udpEndpoint = BindEndpoint.ForUdp("127.0.0.1", udpPort);
    Check("Udp.RuleRejectsSecondNormal", EndpointConflict.Udp(udpEndpoint, false, [(udpEndpoint, false)]), "同端点的普通会话应判为冲突");

    await a.StartAsync("127.0.0.1", udpPort);
    bool doubleBound = false;
    string udpError = "";
    try { await b.StartAsync("127.0.0.1", udpPort); doubleBound = true; }
    catch (Exception ex) { udpError = ex.Message; }
    // ReuseAddress 让同端口双绑定成功，这正是必须在应用层拦截的原因；本条失败说明系统行为与设计假设不同。
    Check("Udp.DoubleBindSucceedsWithoutRule", doubleBound, udpError);
    udpHub.ReleaseSession(b);
    normal = a;
}

// ============ 场景 7：UDP 两个组播会话同端口 ============
Console.WriteLine("== 7. UDP：两个组播会话同端口 ==");
{
    int multicastPort = FreeUdpPort();
    const string group = "239.255.42.99";
    var m1 = udpHub.CreateSession();
    var m2 = udpHub.CreateSession();
    var multicastEndpoint = BindEndpoint.ForUdp("0.0.0.0", multicastPort);
    Check("Udp.RuleAllowsMulticastPair", !EndpointConflict.Udp(multicastEndpoint, true, [(multicastEndpoint, true)]), "组播会话之间应放行");

    bool pairStarted = true;
    string pairError = "";
    try
    {
        await m1.StartAsync("0.0.0.0", multicastPort, group, multicastPort);
        await m2.StartAsync("0.0.0.0", multicastPort, group, multicastPort);
    }
    catch (Exception ex) { pairStarted = false; pairError = ex.Message; }
    Check("Udp.MulticastPairBothStart", pairStarted && m1.IsRunning && m2.IsRunning, pairError);
    // 不断言"两个组播会话各收一份"：UdpService 关闭了组播环回，Windows 上接收端因此收不到本机发出的组播包。

    udpHub.Dispose();
    Check("UdpHub.DisposeStopsSessions", !normal.IsRunning && !m1.IsRunning && !m2.IsRunning,
        $"normal={normal.IsRunning} m1={m1.IsRunning} m2={m2.IsRunning}");
}

Console.WriteLine();
Console.WriteLine($"合计: {pass} 通过, {failures.Count} 失败");
if (failures.Count > 0)
{
    foreach (var f in failures) Console.WriteLine("  " + f);
    return 1;
}
return 0;
