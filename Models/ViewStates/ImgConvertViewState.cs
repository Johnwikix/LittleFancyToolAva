namespace LittleFancyToolAva.Models.ViewStates;

public class ImgConvertViewState
{
    public int FormatIndex { get; set; }
    public string? OutputFolder { get; set; }
    public bool IsDownscaleEnabled { get; set; }
    public int DownscalePercent { get; set; } = 100;
    public int SelectedFilterIndex { get; set; }
}
