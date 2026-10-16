using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FancyToolAva.Models;

public enum VideoContainer
{
    Mp4 = 0,
    Mkv = 1,
    Mov = 2,
    Avi = 3,
    Gif = 4
}

public enum VideoCodec
{
    H264 = 0,       // libx264
    H265 = 1,       // libx265
    Av1Aom = 2,     // libaom-av1
    Av1Svt = 3,     // libsvtav1
    Gif = 4,
    Vp8 = 5,        // libvpx
    Vp9 = 6,        // libvpx-vp9
    Mpeg4 = 7       // mpeg4
}

public enum HardwareBackend
{
    Software = 0,
    Nvidia = 1, // NVENC
    Intel = 2,  // QSV on Windows, VAAPI on Linux
    Amd = 3     // AMF on Windows, VAAPI on Linux
}

public enum AudioCodec
{
    Aac = 0,
    Mp3 = 1,
    Opus = 2,
    Vorbis = 3,
    Flac = 4,
    Ac3 = 5,
    None = 6
}

public enum PresetLevel
{
    Ultrafast = 0,
    Superfast = 1,
    Veryfast = 2,
    Faster = 3,
    Fast = 4,
    Medium = 5,
    Slow = 6,
    Slower = 7,
    Veryslow = 8,
    Placebo = 9
}

public enum VideoPreset
{
    Custom = 0,
    Fast1080p30 = 1,
    HQ1080p30 = 2,
    Fast720p30 = 3,
    HQ720p30 = 4,
    Fast480p30 = 5,
    Gif480p = 6,
    Fast1440p30 = 8,
    HQ1440p30 = 9,
    Fast2160p30 = 10,
    HQ2160p30 = 11,
    Fast1080p30X265 = 12,
    HQ1080p30X265 = 13,
    Fast720p30X265 = 14,
    HQ720p30X265 = 15,
    Fast480p30X265 = 16,
    Fast1440p30X265 = 17,
    HQ1440p30X265 = 18,
    Fast2160p30X265 = 19,
    HQ2160p30X265 = 20
}

public enum RateControlMode
{
    Crf = 0,
    Bitrate = 1
}

public enum ScaleMode
{
    None = 0,
    FitWithin = 1,   // keep aspect, fit within WxH
    Exact = 2,       // force exact WxH (may stretch)
    Width = 3,       // fix width, auto height
    Height = 4       // fix height, auto width
}

public enum FpsMode
{
    SameAsSource = 0,
    Fixed = 1,
    Peak = 2 // -r as max
}

public enum DeinterlaceMode
{
    None = 0,
    Yadif = 1,
    Bwdif = 2
}

public enum DenoiseMode
{
    None = 0,
    Hqdn3dLight = 1,
    Hqdn3dMedium = 2,
    Hqdn3dStrong = 3
}

public enum GifDither
{
    None = 0,
    Bayer = 1,
    FloydSteinberg = 2,
    Sierpinski = 3
}

public enum VideoHdrOutput
{
    SameAsSource = 0,
    ForceHdr = 1,
    ForceSdr = 2
}

public sealed record VideoProbeInfo(
    string FilePath,
    long DurationMs,
    int Width,
    int Height,
    double Fps,
    string VideoCodec,
    string AudioCodec,
    string Container,
    long BitRate,
    bool HasAudio,
    bool HasVideo
)
{
    public string PixelFormat { get; init; } = string.Empty;
    public int BitDepth { get; init; }
    public string ColorPrimaries { get; init; } = string.Empty;
    public string ColorTransfer { get; init; } = string.Empty;
    public string ColorSpace { get; init; } = string.Empty;
    public string ColorRange { get; init; } = string.Empty;
    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";
    public string Display => HasVideo ? $"{Width}x{Height} {Fps:0.##}fps {VideoCodec} / {AudioCodec} {DurationMs / 1000.0:0.0}s{(IsHdr ? " · HDR" : "")}" : "No video stream";
}

public sealed class VideoTranscodeOptions
{
    /// <summary>Gets or sets how intermediate frames are generated.</summary>
    public Services.VideoInterpolationMode InterpolationMode { get; set; }
    /// <summary>Gets or sets the RIFE model used for interpolation.</summary>
    public Services.VideoInterpolationModel InterpolationModel { get; set; }
    /// <summary>Gets or sets the order used when interpolation and super-resolution are combined.</summary>
    public Services.VideoFrameProcessingOrder FrameProcessingOrder { get; set; }
    /// <summary>Gets or sets the source frame rate multiplier, two or four, or zero to use FpsValue.</summary>
    public int InterpolationMultiplier { get; set; } = 2;
    /// <summary>Gets or sets the preferred interpolation accelerator.</summary>
    public Services.VideoSrBackend InterpolationBackend { get; set; }
    /// <summary>Gets or sets whether software inference is permitted after hardware failure.</summary>
    public bool InterpolationAllowCpuFallback { get; set; }
    /// <summary>Gets or sets whether scene changes hold the previous frame instead of synthesizing motion.</summary>
    public bool InterpolationSceneDetection { get; set; } = true;
    public VideoHdrOutput HdrOutput { get; set; }
    /// <summary>Gets or sets whether decoded video frames are enhanced before output scaling and encoding.</summary>
    public bool SuperResolutionEnabled { get; set; }
    /// <summary>Gets or sets the neural model used for frame enhancement.</summary>
    public Services.VideoSrModel SuperResolutionModel { get; set; } = Services.VideoSrModel.Anime4KCnnS;
    /// <summary>Gets or sets the enhancement scale, either two or four.</summary>
    public int SuperResolutionScale { get; set; } = 2;
    /// <summary>Gets or sets the preferred inference accelerator.</summary>
    public Services.VideoSrBackend SuperResolutionBackend { get; set; }
    /// <summary>Gets or sets whether a validated FP16 graph is used for GPU inference.</summary>
    public bool SuperResolutionUseFp16 { get; set; }
    /// <summary>Gets or sets the inclusive trim start on the source timeline, in seconds.</summary>
    public double TrimStartSeconds { get; set; }

    /// <summary>Gets or sets the exclusive trim end in seconds, or null to keep the source ending.</summary>
    public double? TrimEndSeconds { get; set; }

    /// <summary>Copies the encoding settings with an independent per-file trim range.</summary>
    internal VideoTranscodeOptions WithTrim(double startSeconds, double? endSeconds)
    {
        var copy = (VideoTranscodeOptions)MemberwiseClone();
        copy.TrimStartSeconds = startSeconds;
        copy.TrimEndSeconds = endSeconds;
        return copy;
    }

    public VideoContainer Container { get; set; } = VideoContainer.Mp4;
    public VideoCodec VideoCodec { get; set; } = VideoCodec.H264;
    public AudioCodec AudioCodec { get; set; } = AudioCodec.Aac;
    public RateControlMode RateControl { get; set; } = RateControlMode.Crf;
    public int Crf { get; set; } = 23;
    public int VideoBitrateKbps { get; set; } = 2500;
    public bool TwoPassEnabled { get; set; }
    public int AudioBitrateKbps { get; set; } = 128;
    public PresetLevel Preset { get; set; } = PresetLevel.Medium;
    public HardwareBackend HardwareBackend { get; set; } = HardwareBackend.Software;
    public string Profile { get; set; } = ""; // e.g. high, main
    public string Level { get; set; } = "";
    public double AudioGainDb { get; set; } = 0; // -30..30 dB, 0 = passthrough

    // Filters
    public ScaleMode ScaleMode { get; set; } = ScaleMode.None;
    public int ScaleWidth { get; set; } = 1920;
    public int ScaleHeight { get; set; } = 1080;
    public bool KeepAspect { get; set; } = true;

    public bool CropEnabled { get; set; }
    public int CropTop { get; set; }
    public int CropBottom { get; set; }
    public int CropLeft { get; set; }
    public int CropRight { get; set; }

    public FpsMode FpsMode { get; set; } = FpsMode.SameAsSource;
    public double FpsValue { get; set; } = 30;

    public DeinterlaceMode Deinterlace { get; set; } = DeinterlaceMode.None;
    public DenoiseMode Denoise { get; set; } = DenoiseMode.None;

    // GIF specific
    public int GifWidth { get; set; } = 480;
    public int GifFps { get; set; } = 15;
    public int GifMaxColors { get; set; } = 256;
    public GifDither GifDither { get; set; } = GifDither.Bayer;
    public int GifLoop { get; set; } = 0; // 0 infinite
    public string GifStatsMode { get; set; } = "diff"; // diff / single
}

public partial class VideoFileItem : ObservableObject
{
    /// <summary>Gets or sets the trim start for this file, in seconds.</summary>
    public double TrimStartSeconds { get; set; }

    /// <summary>Gets or sets the trim end for this file, or null to keep its entire ending.</summary>
    public double? TrimEndSeconds { get; set; }

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>“添加文件夹”时该文件所属的根目录；输出到指定目录时按相对路径镜像子目录。单独添加的文件为 null。</summary>
    public string? RootFolder { get; init; }

    private FileStatus _status = FileStatus.Pending;
    private string? _errorMessage;
    private double _progress;
    private string _statusDisplay = "";
    private string _progressDisplay = "";
    private VideoProbeInfo? _probeInfo;

    public FileStatus Status
    {
        get => _status;
        set { if (SetProperty(ref _status, value)) RefreshStatusDisplay(); }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set { if (SetProperty(ref _errorMessage, value)) RefreshStatusDisplay(); }
    }

    public double Progress
    {
        get => _progress;
        set { if (SetProperty(ref _progress, value)) RefreshProgressDisplay(); }
    }

    public VideoProbeInfo? ProbeInfo
    {
        get => _probeInfo;
        set { if (SetProperty(ref _probeInfo, value)) { OnPropertyChanged(nameof(ProbeDisplay)); OnPropertyChanged(nameof(DurationDisplay)); } }
    }

    public string ProbeDisplay => ProbeInfo?.Display ?? "—";
    public string DurationDisplay => ProbeInfo != null ? $"{TimeSpan.FromMilliseconds(ProbeInfo.DurationMs):mm\\:ss}" : "";

    public VideoFileItem(string filePath, string? rootFolder = null)
    {
        FilePath = filePath;
        RootFolder = rootFolder;
        RefreshStatusDisplay();
    }

    public void RefreshLocalization()
    {
        RefreshStatusDisplay();
        RefreshProgressDisplay();
        OnPropertyChanged(nameof(ProbeDisplay));
    }

    [RelayCommand]
    private void Remove()
    {
        Owner?.Remove(this);
    }

    internal IVideoFileItemOwner? Owner { get; set; }

    public string StatusDisplay => _statusDisplay;
    public string ProgressDisplay => _progressDisplay;

    private void RefreshStatusDisplay()
    {
        _statusDisplay = _status switch
        {
            FileStatus.Pending => FancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Pending"),
            FileStatus.Converting => FancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Converting"),
            FileStatus.Completed => FancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Done"),
            FileStatus.Failed => string.IsNullOrEmpty(_errorMessage)
                ? FancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Failed")
                : FancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_FailedWithError", _errorMessage),
            _ => ""
        };
        OnPropertyChanged(nameof(StatusDisplay));
    }

    private void RefreshProgressDisplay()
    {
        _progressDisplay = _status switch
        {
            FileStatus.Converting => $" {_progress * 100:F0}%",
            FileStatus.Completed => " 100%",
            _ => ""
        };
        OnPropertyChanged(nameof(ProgressDisplay));
    }
}

internal interface IVideoFileItemOwner
{
    void Remove(VideoFileItem item);
}
