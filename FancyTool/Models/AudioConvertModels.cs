using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FancyToolAva.Models;

/// <summary>输出音频格式（music_player 的 FFmpegAudioConverter 7 种之上新增 AIFF/WMA/AC3/MP2/32 位 WAV）。</summary>
public enum AudioConvertFormat
{
    Mp3 = 0,      // libmp3lame
    AacM4a = 1,   // aac → .m4a
    Flac = 2,     // flac 16/24bit
    AlacM4a = 3,  // alac → .m4a
    Wav = 4,      // PCM 16/24/32int/32float
    Aiff = 5,     // PCM big-endian
    OggVorbis = 6,
    Opus = 7,     // .opus
    Wma = 8,      // wmav2 → .wma
    Ac3 = 9,
    Mp2 = 10
}

/// <summary>无损 PCM 输出的位深选择（Auto 按源位深推断，规则与 music_player 转换器一致）。</summary>
public enum AudioBitDepthMode
{
    Auto = 0,
    S16 = 1,
    S24 = 2,
    S32 = 3,
    F32 = 4
}

public sealed record AudioProbeInfo(
    string FilePath,
    string Codec,
    int SampleRate,
    int Channels,
    int BitDepth,
    int BitRateKbps,
    long DurationMs,
    bool IsDsd
)
{
    public string Display =>
        (SampleRate > 0 ? $"{(IsDsd ? "DSD" : $"{SampleRate / 1000.0:0.##}kHz")} {Channels}ch " : "") +
        (BitDepth > 0 ? $"{BitDepth}bit " : "") +
        (!string.IsNullOrEmpty(Codec) ? Codec.ToUpperInvariant() + " " : "") +
        (BitRateKbps > 0 ? $"{BitRateKbps}kbps " : "") +
        $"{DurationMs / 1000.0:0.0}s";
}

public sealed class AudioConvertOptions
{
    public AudioConvertFormat Format { get; set; } = AudioConvertFormat.Mp3;
    public int BitrateKbps { get; set; } = 320;
    public AudioBitDepthMode BitDepth { get; set; } = AudioBitDepthMode.Auto;
    /// <summary>0 = 跟随源（编码器支持表内自动对齐）。</summary>
    public int SampleRateHz { get; set; }
    public double GainDb { get; set; }
    /// <summary>把源的标签/封面（ATL 读取）内联写入输出（wav/aiff 不写）。</summary>
    public bool CopyMetadata { get; set; } = true;
}

public partial class AudioFileItem : ObservableObject
{
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);

    private FileStatus _status = FileStatus.Pending;
    private string? _errorMessage;
    private double _progress;
    private string _statusDisplay = "";
    private string _progressDisplay = "";
    private AudioProbeInfo? _probeInfo;

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

    public AudioProbeInfo? ProbeInfo
    {
        get => _probeInfo;
        set { if (SetProperty(ref _probeInfo, value)) OnPropertyChanged(nameof(ProbeDisplay)); }
    }

    public string ProbeDisplay => ProbeInfo?.Display ?? "—";

    public AudioFileItem(string filePath) { FilePath = filePath; RefreshStatusDisplay(); }

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

    internal IAudioFileItemOwner? Owner { get; set; }

    public string StatusDisplay => _statusDisplay;
    public string ProgressDisplay => _progressDisplay;

    private void RefreshStatusDisplay()
    {
        _statusDisplay = _status switch
        {
            FileStatus.Pending => Services.LocalizationRegistry.Get("FileItem.Status_Pending"),
            FileStatus.Converting => Services.LocalizationRegistry.Get("FileItem.Status_Converting"),
            FileStatus.Completed => Services.LocalizationRegistry.Get("FileItem.Status_Done"),
            FileStatus.Failed => string.IsNullOrEmpty(_errorMessage)
                ? Services.LocalizationRegistry.Get("FileItem.Status_Failed")
                : Services.LocalizationRegistry.Get("FileItem.Status_FailedWithError", _errorMessage),
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

internal interface IAudioFileItemOwner
{
    void Remove(AudioFileItem item);
}
