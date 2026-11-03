namespace FancyToolAva.Services;

/// <summary>The small preferences surface used by super-resolution services; the desktop app binds it to AppPreferences.</summary>
public interface ISuperResolutionPreferences
{
    bool UseSuperResolutionNpu { get; set; }
    int SuperResolutionTileSizeIndex { get; set; }
}

public sealed class DefaultSuperResolutionPreferences : ISuperResolutionPreferences
{
    public bool UseSuperResolutionNpu { get; set; } = true;
    public int SuperResolutionTileSizeIndex { get; set; } = 2;
}
