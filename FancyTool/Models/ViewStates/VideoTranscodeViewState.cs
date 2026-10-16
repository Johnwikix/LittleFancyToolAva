namespace FancyToolAva.Models.ViewStates;

public class VideoTranscodeViewState
{
    /// <summary>Gets or sets the interpolation method index.</summary>
    public int InterpolationModeIndex { get; set; }
    /// <summary>Gets or sets the selected RIFE model index.</summary>
    public int InterpolationModelIndex { get; set; }
    /// <summary>Gets or sets the combined frame processing order.</summary>
    public int InterpolationOrderIndex { get; set; }
    /// <summary>Gets or sets the twofold, fourfold, or sixfold frame rate multiplier index.</summary>
    public int InterpolationRateIndex { get; set; }
    /// <summary>Gets or sets the rate catalog version used to migrate obsolete custom target selections.</summary>
    public int InterpolationRateVersion { get; set; }
    /// <summary>Gets or sets the interpolation accelerator preference.</summary>
    public int InterpolationBackendIndex { get; set; }
    /// <summary>Gets or sets whether software interpolation fallback is allowed.</summary>
    public bool InterpolationAllowCpuFallback { get; set; }
    /// <summary>Gets or sets whether scene cuts suppress generated motion.</summary>
    public bool InterpolationSceneDetection { get; set; } = true;
    public int HdrOutputIndex { get; set; }
    /// <summary>Gets or sets whether video super-resolution is enabled.</summary>
    public bool IsVideoSrEnabled { get; set; }
    /// <summary>Gets or sets the selected video enhancement model.</summary>
    public int VideoSrModelIndex { get; set; }
    /// <summary>Gets or sets the selected enhancement scale.</summary>
    public int VideoSrScaleIndex { get; set; }
    /// <summary>Gets or sets the preferred accelerator.</summary>
    public int VideoSrBackendIndex { get; set; }
    /// <summary>Gets or sets the video model catalog version used to migrate obsolete selections.</summary>
    public int VideoSrCatalogVersion { get; set; }
    /// <summary>Gets or sets whether validated FP16 GPU inference is selected.</summary>
    public bool VideoSrUseFp16 { get; set; } = true;
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
    public double AudioGainDb { get; set; } = 0;
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
