// TODO: 待完善 — 功能可用但暂不启用，后续可完善后重新接入导航
namespace LittleFancyToolAva.Models.ViewStates;

public class ModbusPollViewState
{
    public string SelectedPort { get; set; } = string.Empty;
    public int BaudRateIndex { get; set; } = 4;
    public int ParityIndex { get; set; }
    public int DataBitsIndex { get; set; } = 3;
    public int StopBitsIndex { get; set; }
    public int FunctionCodeIndex { get; set; } = 2;
    public string SlaveId { get; set; } = "1";
    public string StartAddress { get; set; } = "0";
    public string Quantity { get; set; } = "10";
    public string ScanTime { get; set; } = "1000";
    public string WriteAddress { get; set; } = "0";
    public string WriteValue { get; set; } = "0";
}
