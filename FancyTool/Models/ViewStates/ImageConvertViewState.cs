namespace FancyToolAva.Models.ViewStates;

public class ImageConvertViewState
{
    public int FormatIndex { get; set; }
    public string? OutputFolder { get; set; }
    public bool IsDownscaleEnabled { get; set; }
    public int DownscalePercent { get; set; } = 100;
    public int SelectedFilterIndex { get; set; }
    public int SelectedSizeIndex { get; set; } = 2;
    public bool IsSuperResolutionEnabled { get; set; }
    // 保留历史字段名；值为 SuperResolutionModel 的稳定编号，不是当前下拉框索引。
    public int SelectedSrModelIndex { get; set; } = 4;
    public int SelectedSrScaleIndex { get; set; }
}
