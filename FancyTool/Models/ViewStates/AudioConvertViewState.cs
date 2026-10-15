namespace FancyToolAva.Models.ViewStates;

public class AudioConvertViewState
{
    public string? OutputFolder { get; set; }
    public string? SelectedFormat { get; set; }
    public int FormatIndex { get; set; }
    public string? SelectedBitDepth { get; set; }
    public string? SelectedSampleRate { get; set; }
    public int BitrateKbps { get; set; } = 320;
    /// <summary>Gets or sets a value indicating whether spectral enhancement is enabled.</summary>
    public bool EnableSpectralEnhancement { get; set; }
    public double GainDb { get; set; }
    public bool CopyMetadata { get; set; } = true;
    public bool IncludeAllInFolderScan { get; set; }
}
