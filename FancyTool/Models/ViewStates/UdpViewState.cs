namespace FancyToolAva.Models.ViewStates;

public class UdpViewState
{
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
