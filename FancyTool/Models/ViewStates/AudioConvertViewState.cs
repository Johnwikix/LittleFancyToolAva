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
    /// <summary>Gets or sets the persisted multiplier; older settings retain the previous 2× default.</summary>
    public int UpsamplingMultiplier { get; set; } = 2;
    /// <summary>Gets or sets the backend identifier without loading a backend on restore.</summary>
    public string UpsamplingBackendId { get; set; } = AudioEnhancement.AudioUpsamplingBackendRegistry.DefaultBackendId;
    public double GainDb { get; set; }
    public bool CopyMetadata { get; set; } = true;
    public bool IncludeAllInFolderScan { get; set; }
}
