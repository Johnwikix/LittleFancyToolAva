namespace FancyToolAva.Models.ViewStates;

/// <summary>
/// 串口调试页面状态（v2）：保存多个会话、当前选中会话。
/// 旧的 v1 扁平字段保留用于迁移，读到即转换为单会话。
/// </summary>
public class SerialPortViewState
{
    public int SchemaVersion { get; set; } = 2;

    public List<SerialSessionViewState> Sessions { get; set; } = [];

    public string? SelectedSessionId { get; set; }

    // ---- v1 兼容字段（仅迁移读取，不再写回）----
    public string SelectedPort { get; set; } = string.Empty;
    public int BaudRateIndex { get; set; }
    public int ParityIndex { get; set; }
    public int DataBitsIndex { get; set; } = 3;
    public int StopBitsIndex { get; set; }
    public int EncodingIndex { get; set; }
    public string SendText { get; set; } = string.Empty;
    public bool IsHexSend { get; set; }
    public bool IsHexDisplay { get; set; }
    public bool IsRtsEnabled { get; set; }
    public bool IsDtrEnabled { get; set; }
    public int PollInterval { get; set; } = 1000;
    public int FrameBreakInterval { get; set; } = 20;
    public bool EnableSendCount { get; set; }
    public int SendCount { get; set; } = 1;
}

public class SerialSessionViewState
{
    public string Id { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string PortName { get; set; } = string.Empty;
    public int BaudRateIndex { get; set; }
    public int ParityIndex { get; set; }
    public int DataBitsIndex { get; set; } = 3;
    public int StopBitsIndex { get; set; }
    public int EncodingIndex { get; set; }
    public int FrameBreakInterval { get; set; } = 20;
    public bool IsHexSend { get; set; }
    public bool IsHexDisplay { get; set; }
    public bool IsRtsEnabled { get; set; }
    public bool IsDtrEnabled { get; set; }
    public int PollInterval { get; set; } = 1000;
    public bool EnableSendCount { get; set; }
    public int SendCount { get; set; } = 1;
    public string SendText { get; set; } = string.Empty;
}