namespace FancyToolAva.Models.ViewStates;

/// <summary>
/// TCP 调试页面状态（v2）：保存多个会话、当前选中会话与页面级设置。
/// 旧的 v1 扁平字段保留用于迁移，读到即转换为单会话。
/// </summary>
public class TcpServerViewState
{
    public int SchemaVersion { get; set; } = 2;

    public List<TcpSessionViewState> Sessions { get; set; } = [];

    public string? SelectedSessionId { get; set; }

    // ---- 页面级 ----
    public int FunctionIndex { get; set; }
    // 留空表示旧存档没有这两项，迁移时取旧会话的地址与端口。
    public string LinkAddress { get; set; } = string.Empty;
    public string LinkPort { get; set; } = string.Empty;
    public int LinkRoleIndex { get; set; }
    public int LinkDurationSec { get; set; } = 10;
    public int LinkChunkKB { get; set; } = 64;

    // ---- v1 兼容字段（仅迁移读取，不再写回）----
    public int ModeIndex { get; set; }
    public string Address { get; set; } = "127.0.0.1";
    public string Port { get; set; } = "8080";
    public string SendText { get; set; } = string.Empty;
    public bool IsHexSend { get; set; }
    public bool IsHexDisplay { get; set; }
    public bool EnableFrameBreak { get; set; }
    public int FrameBreakInterval { get; set; } = 20;
    public int PollInterval { get; set; } = 1000;
    public bool EnableSendCount { get; set; }
    public int SendCount { get; set; } = 1;
}

public class TcpSessionViewState
{
    public string Id { get; set; } = string.Empty;
    public int ModeIndex { get; set; }
    public string Address { get; set; } = "127.0.0.1";
    public string Port { get; set; } = "8080";
    public string SendText { get; set; } = string.Empty;
    public bool IsHexSend { get; set; }
    public bool IsHexDisplay { get; set; }
    public bool EnableFrameBreak { get; set; }
    public int FrameBreakInterval { get; set; } = 20;
    public int PollInterval { get; set; } = 1000;
    public bool EnableSendCount { get; set; }
    public int SendCount { get; set; } = 1;
}
