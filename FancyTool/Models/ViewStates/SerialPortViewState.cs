namespace FancyToolAva.Models.ViewStates;

public class SerialPortViewState
{
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
