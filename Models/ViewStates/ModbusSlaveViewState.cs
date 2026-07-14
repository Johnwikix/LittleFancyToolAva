namespace LittleFancyToolAva.Models.ViewStates;

public class ModbusSlaveViewState
{
    public string SelectedPort { get; set; } = string.Empty;
    public int BaudRateIndex { get; set; } = 4;
    public int ParityIndex { get; set; }
    public int DataBitsIndex { get; set; } = 3;
    public int StopBitsIndex { get; set; }
    public string SlaveId { get; set; } = "1";
    public string CoilCount { get; set; } = "16";
    public string RegisterCount { get; set; } = "16";
}
