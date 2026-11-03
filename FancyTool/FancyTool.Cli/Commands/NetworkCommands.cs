using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FancyToolAva.Utils;

namespace FancyToolAva.Cli.Commands;

internal static class NetworkCommands
{
    public static void Register(Dictionary<string, Func<CliContext, Task<int>>> map)
    {
        map["serial list"] = SerialListAsync;
        map["serial send"] = ctx => SerialAsync(ctx, listenOnly: false);
        map["serial listen"] = ctx => SerialAsync(ctx, listenOnly: true);
        map["tcp server"] = ctx => TcpAsync(ctx, server: true);
        map["tcp client"] = ctx => TcpAsync(ctx, server: false);
        map["udp bind"] = UdpBindAsync;
        map["udp send"] = UdpSendAsync;
        map["linktest tcp-server"] = ctx => LinkTestAsync(ctx, tcp: true, server: true);
        map["linktest tcp-client"] = ctx => LinkTestAsync(ctx, tcp: true, server: false);
        map["linktest udp-server"] = ctx => LinkTestAsync(ctx, tcp: false, server: true);
        map["linktest udp-client"] = ctx => LinkTestAsync(ctx, tcp: false, server: false);
    }

    private static async Task<int> SerialListAsync(CliContext ctx)
    {
        var service = ctx.Get<ISerialPortService>();
        string[] ports = service.GetPortNames();
        if (ctx.Json) ctx.WriteJson(ports);
        else if (ports.Length == 0) ctx.WriteResult(ctx.Zh ? "未发现串口。" : "No serial ports found.");
        else ctx.WriteResult(string.Join(Environment.NewLine, ports));
        await Task.CompletedTask;
        return 0;
    }

    private static async Task<int> SerialAsync(CliContext ctx, bool listenOnly)
    {
        string port = ctx.Args.Require("--port", ctx.Zh ? "需要 --port。" : "--port is required.");
        int baud = ctx.Args.GetInt("--baud", 115200);
        string encodingName = ctx.Args.GetChoice("--encoding", "utf8", "auto", "utf8", "ascii", "gb2312")!;
        string parityName = ctx.Args.GetChoice("--parity", "none", "none", "odd", "even", "mark", "space")!;
        int dataBits = ctx.Args.GetInt("--data-bits", 8);
        string stopName = ctx.Args.GetChoice("--stop-bits", "one", "one", "two", "onepointfive")!;
        Parity parity = parityName switch
        {
            "odd" => Parity.Odd,
            "even" => Parity.Even,
            "mark" => Parity.Mark,
            "space" => Parity.Space,
            _ => Parity.None,
        };
        StopBits stopBits = stopName switch
        {
            "two" => StopBits.Two,
            "onepointfive" => StopBits.OnePointFive,
            _ => StopBits.One,
        };
        bool hex = ctx.Args.GetBool("--hex");
        string sendData = ctx.Args.Get("--data") ?? string.Empty;
        if (hex && sendData.Length > 0 && !ToolMethod.TryHexStringToBytes(sendData, out _))
            throw new CliUsageException(ctx.Zh ? "--data 不是有效的十六进制。" : "--data is not valid hexadecimal.");
        bool shouldListen = listenOnly || ctx.Args.Has("--listen") || ctx.Args.Has("--duration") || sendData.Length == 0;
        double duration = ctx.Args.GetDouble("--duration", 10);
        Encoding displayEncoding = encodingName switch
        {
            "ascii" => Encoding.ASCII,
            "gb2312" => Encoding.GetEncoding("GB18030"),
            _ => Encoding.UTF8,
        };
        string sendEncoding = encodingName switch
        {
            "utf8" or "auto" => "UTF8",
            "ascii" => "ASCII",
            "gb2312" => "GB2312",
            _ => "UTF8",
        };

        var service = ctx.Get<ISerialPortService>();
        var outputLock = new object();
        void Write(string kind, byte[] bytes)
        {
            if (bytes.Length == 0) return;
            lock (outputLock)
            {
                if (ctx.Json)
                {
                    ctx.WriteJsonLine(new { type = kind, hex = ToolMethod.ByteArrayToSpacedHexString(bytes), text = displayEncoding.GetString(bytes) });
                }
                else
                {
                    string payload = hex ? ToolMethod.ByteArrayToSpacedHexString(bytes) : displayEncoding.GetString(bytes);
                    Console.Out.WriteLine($"{(kind == "rx" ? "RX" : "TX")}> {payload}");
                }
            }
        }
        service.BytesReceived += b => Write("rx", b);
        service.DataSent += b => Write("tx", b);
        service.ConnectionStateChanged += (_, e) =>
        {
            if (ctx.Quiet) return;
            if (e.Type is ConnectionEventType.Error or ConnectionEventType.Lost)
                ctx.Info(e.Message);
        };
        if (ctx.Args.GetInt("--frame-break-ms", 0) is var frameBreak && frameBreak > 0)
            service.SetFrameBreakInterval(frameBreak);

        await service.OpenAsync(port, baud, parity, dataBits, stopBits, ctx.CancellationToken).ConfigureAwait(false);
        try
        {
            if (sendData.Length > 0)
                await service.SendAsync(sendData, hex, sendEncoding).ConfigureAwait(false);
            if (shouldListen)
            {
                if (duration > 0)
                    await Task.Delay(TimeSpan.FromSeconds(duration), ctx.CancellationToken).ConfigureAwait(false);
                else
                    await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            service.Close();
        }
        return 0;
    }

    private static async Task<int> TcpAsync(CliContext ctx, bool server)
    {
        int port = ctx.Args.GetInt("--port", 0);
        if (port is < 1 or > 65535) throw new CliUsageException("--port must be 1..65535.");
        bool hex = ctx.Args.GetBool("--hex");
        string? sendData = ctx.Args.Get("--send");
        if (hex && sendData is not null && !ToolMethod.TryHexStringToBytes(sendData, out _))
            throw new CliUsageException(ctx.Zh ? "--send 不是有效的十六进制。" : "--send is not valid hexadecimal.");
        double duration = ctx.Args.GetDouble("--duration", 10);
        var service = ctx.Get<ITcpServerService>();
        var firstClient = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputLock = new object();
        service.BytesReceived += b => WriteEvent(ctx, outputLock, "rx", b, hex);
        service.DataSent += b => WriteEvent(ctx, outputLock, "tx", b, hex);
        service.ConnectionStateChanged += (_, e) =>
        {
            if (e.Type == ConnectionEventType.ClientConnected) firstClient.TrySetResult();
            if (!ctx.Quiet && e.Type is ConnectionEventType.Error or ConnectionEventType.Lost or ConnectionEventType.ClientConnected or ConnectionEventType.ClientDisconnected)
                ctx.Info(e.Message);
        };

        if (server)
        {
            string bind = ctx.Args.Get("--bind") ?? "0.0.0.0";
            await service.StartServerAsync(bind, port, ctx.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            string host = ctx.Args.Require("--host", ctx.Zh ? "需要 --host。" : "--host is required.");
            await service.ConnectClientAsync(host, port, ctx.CancellationToken).ConfigureAwait(false);
            firstClient.TrySetResult();
        }

        try
        {
            if (sendData is not null)
            {
                if (server)
                    await firstClient.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, duration)), ctx.CancellationToken).ConfigureAwait(false);
                await service.SendAsync(sendData, hex).ConfigureAwait(false);
            }
            if (duration > 0)
                await Task.Delay(TimeSpan.FromSeconds(duration), ctx.CancellationToken).ConfigureAwait(false);
            else
                await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (server) service.StopServer();
            else service.DisconnectClient();
        }
        return 0;
    }

    private static async Task<int> UdpBindAsync(CliContext ctx)
    {
        int port = ctx.Args.GetInt("--port", 0);
        if (port is < 1 or > 65535) throw new CliUsageException("--port must be 1..65535.");
        string bind = ctx.Args.Get("--bind") ?? "0.0.0.0";
        string? multicast = ctx.Args.Get("--multicast");
        int? multicastPort = ctx.Args.GetNullableInt("--multicast-port");
        bool hex = ctx.Args.GetBool("--hex");
        double duration = ctx.Args.GetDouble("--duration", 10);
        var service = ctx.Get<IUdpService>();
        var outputLock = new object();
        service.BytesReceived += b => WriteEvent(ctx, outputLock, "rx", b, hex);
        service.DataSent += b => WriteEvent(ctx, outputLock, "tx", b, hex);
        service.ConnectionStateChanged += (_, e) =>
        {
            if (!ctx.Quiet && e.Type is ConnectionEventType.Error or ConnectionEventType.Lost) ctx.Info(e.Message);
        };
        await service.StartAsync(bind, port, multicast, multicastPort, ctx.CancellationToken).ConfigureAwait(false);
        try
        {
            if (duration > 0)
                await Task.Delay(TimeSpan.FromSeconds(duration), ctx.CancellationToken).ConfigureAwait(false);
            else
                await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            service.Stop();
        }
        return 0;
    }

    private static async Task<int> UdpSendAsync(CliContext ctx)
    {
        string host = ctx.Args.Require("--host", ctx.Zh ? "需要 --host。" : "--host is required.");
        int port = ctx.Args.GetInt("--port", 0);
        if (port is < 1 or > 65535) throw new CliUsageException("--port must be 1..65535.");
        string data = ctx.Args.Require("--data", ctx.Zh ? "需要 --data。" : "--data is required.");
        bool hex = ctx.Args.GetBool("--hex");
        byte[] bytes;
        if (hex)
        {
            if (!ToolMethod.TryHexStringToBytes(data, out bytes) || bytes.Length == 0)
                throw new CliUsageException(ctx.Zh ? "--data 不是有效的十六进制。" : "--data is not valid hexadecimal.");
        }
        else
        {
            bytes = Encoding.UTF8.GetBytes(data);
        }

        IPAddress address = await ResolveAsync(host).ConfigureAwait(false);
        using var client = ctx.Args.GetNullableInt("--bind-port") is { } bindPort
            ? new UdpClient(new IPEndPoint(IPAddress.Any, bindPort))
            : new UdpClient();
        client.EnableBroadcast = true;
        await client.SendAsync(bytes, new IPEndPoint(address, port), ctx.CancellationToken).ConfigureAwait(false);
        if (ctx.Json) ctx.WriteJson(new { host, port, bytes = bytes.Length });
        else ctx.WriteResult(ctx.Zh ? $"已发送 {bytes.Length} 字节。" : $"Sent {bytes.Length} bytes.");
        return 0;
    }

    private static async Task<int> LinkTestAsync(CliContext ctx, bool tcp, bool server)
    {
        int port = ctx.Args.GetInt("--port", 0);
        if (port is < 1 or > 65535) throw new CliUsageException("--port must be 1..65535.");
        var completion = new TaskCompletionSource<LinkTestSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<LinkTestSnapshot>? onSnapshot = null;
        Action<LinkTestSummary> onCompleted = s => completion.TrySetResult(s);
        Action<string> onFailed = message => completion.TrySetException(new InvalidOperationException(message));
        Action<LinkTestPhase> onPhase = _ => { };

        if (tcp)
        {
            var service = ctx.Get<TcpLinkTestService>();
            service.SnapshotUpdated += s => onSnapshot?.Invoke(s);
            service.Completed += onCompleted;
            service.Failed += onFailed;
            service.PhaseChanged += onPhase;
            try
            {
                if (server) await service.StartReceiverAsync(ctx.Args.Get("--bind") ?? "0.0.0.0", port).ConfigureAwait(false);
                else await service.StartSenderAsync(ctx.Args.Require("--host", ctx.Zh ? "需要 --host。" : "--host is required."), port, LinkOptions(ctx)).ConfigureAwait(false);
                onSnapshot = s => PrintSnapshot(ctx, s);
                LinkTestSummary summary = await WaitLinkAsync(ctx, completion.Task).ConfigureAwait(false);
                PrintSummary(ctx, summary);
                return 0;
            }
            finally
            {
                await service.StopAsync().ConfigureAwait(false);
                service.Dispose();
            }
        }
        else
        {
            var service = ctx.Get<UdpLinkTestService>();
            service.SnapshotUpdated += s => onSnapshot?.Invoke(s);
            service.Completed += onCompleted;
            service.Failed += onFailed;
            service.PhaseChanged += onPhase;
            try
            {
                if (server) await service.StartReceiverAsync(ctx.Args.Get("--bind") ?? "0.0.0.0", port).ConfigureAwait(false);
                else await service.StartSenderAsync(ctx.Args.Require("--host", ctx.Zh ? "需要 --host。" : "--host is required."), port, LinkOptions(ctx)).ConfigureAwait(false);
                onSnapshot = s => PrintSnapshot(ctx, s);
                LinkTestSummary summary = await WaitLinkAsync(ctx, completion.Task).ConfigureAwait(false);
                PrintSummary(ctx, summary);
                return 0;
            }
            finally
            {
                await service.StopAsync().ConfigureAwait(false);
                service.Dispose();
            }
        }
    }

    private static LinkTestOptions LinkOptions(CliContext ctx) => new()
    {
        DurationSec = ctx.Args.GetInt("--duration", 10),
        ChunkSize = ctx.Args.GetInt("--chunk-size", 64 * 1024),
        RateLimitBps = (long)ctx.Args.GetDouble("--rate", 0),
    };

    private static async Task<LinkTestSummary> WaitLinkAsync(CliContext ctx, Task<LinkTestSummary> task)
    {
        return await task.WaitAsync(ctx.CancellationToken).ConfigureAwait(false);
    }

    private static void PrintSnapshot(CliContext ctx, LinkTestSnapshot snapshot)
    {
        if (ctx.Quiet || ctx.Json || Console.IsErrorRedirected) return;
        Console.Error.Write($"\r{snapshot.Phase,-14} {snapshot.TotalBytes,12:N0} B  {snapshot.CurrentBps / 1024 / 1024,8:F2} MB/s");
    }

    private static void PrintSummary(CliContext ctx, LinkTestSummary summary)
    {
        ctx.ProgressDone();
        if (ctx.Json)
        {
            ctx.WriteJson(new
            {
                isSender = summary.IsSender,
                completed = summary.Completed,
                totalBytes = summary.TotalBytes,
                totalPackets = summary.TotalPackets,
                expectedPackets = summary.ExpectedPackets,
                lostPackets = summary.LostPackets,
                lossRate = summary.LossRate,
                elapsedSec = summary.ElapsedSec,
                avgBps = summary.AvgBps,
                hasRemoteStats = summary.HasRemoteStats,
            });
        }
        else
        {
            ctx.WriteResult(
                $"{(ctx.Zh ? "角色" : "Role")}: {(summary.IsSender ? (ctx.Zh ? "发送端" : "sender") : (ctx.Zh ? "接收端" : "receiver"))}\n" +
                $"{(ctx.Zh ? "完成" : "Completed")}: {summary.Completed}\n" +
                $"{(ctx.Zh ? "字节" : "Bytes")}: {summary.TotalBytes:N0}\n" +
                $"{(ctx.Zh ? "平均速率" : "Avg rate")}: {summary.AvgBps / 1024 / 1024:F2} MB/s\n" +
                $"{(ctx.Zh ? "时长" : "Elapsed")}: {summary.ElapsedSec:F2} s\n" +
                (summary.LossRate >= 0 ? $"{(ctx.Zh ? "丢包率" : "Loss")}: {summary.LossRate:P2}\n" : string.Empty));
        }
    }

    private static void WriteEvent(CliContext ctx, object gate, string kind, byte[] bytes, bool hex)
    {
        if (bytes.Length == 0) return;
        lock (gate)
        {
            if (ctx.Json)
            {
                ctx.WriteJsonLine(new { type = kind, hex = ToolMethod.ByteArrayToSpacedHexString(bytes), text = Encoding.UTF8.GetString(bytes) });
            }
            else
            {
                Console.Out.WriteLine($"{(kind == "rx" ? "RX" : "TX")}> {(hex ? ToolMethod.ByteArrayToSpacedHexString(bytes) : Encoding.UTF8.GetString(bytes))}");
            }
        }
    }

    private static async Task<IPAddress> ResolveAsync(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed)) return parsed;
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
    }
}