namespace FancyToolAva.Models.ViewStates;

/// <summary>
/// UDP 调试页面状态（v2）：保存多个会话、当前选中会话与页面级设置。
/// 旧的 v1 扁平字段保留用于迁移，读到即转换为单会话。
/// </summary>
public class UdpViewState
{
    public int SchemaVersion { get; set; } = 2;

    public List<UdpSessionViewState> Sessions { get; set; } = [];

    public string? SelectedSessionId { get; set; }

    // ---- 页面级 ----
    public int FunctionIndex { get; set; }
    // 留空表示旧存档没有链路测试端点，迁移时取旧会话的本地与远程端点。
    public string LinkLocalAddress { get; set; } = string.Empty;
    public string LinkLocalPort { get; set; } = string.Empty;
    public string LinkRemoteAddress { get; set; } = string.Empty;
    public string LinkRemotePort { get; set; } = string.Empty;
    public int LinkRoleIndex { get; set; }
    public int LinkDurationSec { get; set; } = 10;
    public int LinkPacketSize { get; set; } = 1400;
    public int LinkRateMbps { get; set; } = 10;

    // ---- v1 兼容字段（仅迁移读取，不再写回）----
    public int ModeIndex { get; set; }
    public string LocalAddress { get; set; } = "0.0.0.0";
    public string LocalPort { get; set; } = "8080";
    public string MulticastAddress { get; set; } = "239.0.0.1";
    public string MulticastPort { get; set; } = "8080";
    public string RemoteAddress { get; set; } = "127.0.0.1";
    public string RemotePort { get; set; } = "9090";
    public string SendText { get; set; } = string.Empty;
    public bool IsHexSend { get; set; }
    public bool IsHexDisplay { get; set; }
    public int FrameBreakInterval { get; set; } = 20;
    public int PollInterval { get; set; } = 1000;
    public bool EnableSendCount { get; set; }
    public int SendCount { get; set; } = 1;
}

public class UdpSessionViewState
{
    public string Id { get; set; } = string.Empty;
    public int ModeIndex { get; set; }
    public string LocalAddress { get; set; } = "0.0.0.0";
    public string LocalPort { get; set; } = "8080";
    public string MulticastAddress { get; set; } = "239.0.0.1";
    public string MulticastPort { get; set; } = "8080";
    public string RemoteAddress { get; set; } = "127.0.0.1";
    public string RemotePort { get; set; } = "9090";
    public string SendText { get; set; } = string.Empty;
    public bool IsHexSend { get; set; }
    public bool IsHexDisplay { get; set; }
    /// <summary>UDP 服务是空实现、界面也不展示，只为兼容旧存档保留。</summary>
    public int FrameBreakInterval { get; set; } = 20;
    public int PollInterval { get; set; } = 1000;
    public bool EnableSendCount { get; set; }
    public int SendCount { get; set; } = 1;
}
