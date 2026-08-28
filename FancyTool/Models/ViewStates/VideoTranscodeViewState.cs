namespace FancyToolAva.Models.ViewStates;

public class VideoTranscodeViewState
{
    public string? OutputFolder { get; set; }
    public int ContainerIndex { get; set; }
    public int VideoCodecIndex { get; set; }
    public int AudioCodecIndex { get; set; }
    public string? SelectedVideoCodec { get; set; }
    public string? SelectedAudioCodec { get; set; }
    public string? SelectedContainer { get; set; }
    public string? SelectedHardwareBackend { get; set; }
    public string? SelectedRateControl { get; set; }
    public string? SelectedPreset { get; set; }
    public string? SelectedHandbrakePreset { get; set; }
    public string? SelectedScaleMode { get; set; }
    public string? SelectedFpsMode { get; set; }
    public string? SelectedDeinterlace { get; set; }
    public string? SelectedDenoise { get; set; }
    public string? SelectedGifDither { get; set; }
    public string? SelectedGifStats { get; set; }
    public int RateControlIndex { get; set; }
    public int Crf { get; set; } = 23;
    public int VideoBitrateKbps { get; set; } = 2500;
    public bool TwoPassEnabled { get; set; }
    public int HardwareBackend { get; set; }
    public int AudioBitrateKbps { get; set; } = 128;
    public int PresetIndex { get; set; }
    public int PresetPresetIndex { get; set; }
    public int ScaleModeIndex { get; set; }
    public int ScaleWidth { get; set; } = 1920;
    public int ScaleHeight { get; set; } = 1080;
    public bool KeepAspect { get; set; } = true;
    public bool CropEnabled { get; set; }
    public int CropTop { get; set; }
    public int CropBottom { get; set; }
    public int CropLeft { get; set; }
    public int CropRight { get; set; }
    public int FpsModeIndex { get; set; }
    public double FpsValue { get; set; } = 30;
    public int DeinterlaceIndex { get; set; }
    public int DenoiseIndex { get; set; }
    public int GifDitherIndex { get; set; } = 1;
    public int GifMaxColors { get; set; } = 256;
    public int GifFps { get; set; } = 15;
    public int GifWidth { get; set; } = 480;
    public int GifLoop { get; set; } = 0;
    public string GifStatsMode { get; set; } = "diff";
    public int SelectedPresetIndex { get; set; } // Output preset index
    public bool IncludeAllInFolderScan { get; set; }
}
