namespace FancyToolAva.Models.ViewStates;

public class FolderCompareViewState
{
    public string SourceFolder { get; set; } = string.Empty;
    public string TargetFolder { get; set; } = string.Empty;
    public bool UseHashComparison { get; set; } = true;
    public bool UseMusicTitleComparison { get; set; }
}
