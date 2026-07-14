namespace LittleFancyToolAva.Models.ViewStates;

public class TcpServerViewState
{
    public int ModeIndex { get; set; }
    public string Address { get; set; } = "127.0.0.1";
    public string Port { get; set; } = "8080";
    public string SendText { get; set; } = string.Empty;
    public bool IsHexSend { get; set; }
    public bool IsHexDisplay { get; set; }
    public bool EnableFrameBreak { get; set; }
    public int FrameBreakInterval { get; set; } = 20;
    public int PollInterval { get; set; } = 1000;
}
