using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lang.Avalonia;
using FancyToolAva.Models;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.Services;
using FancyToolAva.Utils;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.ViewModels;

public partial class VideoTranscodeViewModel : ViewModelBase, IViewState, IVideoFileItemOwner, IDisposable
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".flv", ".wmv", ".m4v", ".mpg", ".mpeg", ".3gp", ".gif", ".ts", ".mts", ".m2ts", ".webm"
    };

    private static bool IsVideoFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return VideoExtensions.Contains(Path.GetExtension(path));
    }

    private readonly IVideoTranscodeService _videoService;
    private readonly IFfmpegService _ffmpegService;
    private readonly IFileDialogService _fileDialogService;
    private readonly INotificationService _notificationService;
    private readonly IViewStateService _viewStateService;
    private readonly AppPreferences _preferences;
    private CancellationTokenSource? _cts;
    private int _completedCountField;
    private int _failedCountField;
    private bool _isDisposed;
    private bool _isApplyingPreset;
    private bool _suppressPresetReset;
    private bool _probeFailureNotified;

    string IViewState.ViewName => "videoTranscodeView";

    public ObservableCollection<VideoFileItem> FileItems { get; } = [];

    public VideoFileItem? SelectedFileItem
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    public string? OutputFolder
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                InvalidateCanOpenOutputFolder();
                OnPropertyChanged(nameof(IsOutputFolderEmpty));
            }
        }
    }

    public bool IsOutputFolderEmpty => string.IsNullOrWhiteSpace(OutputFolder);

    // Ffmpeg BYO
    public string? FfmpegDirectory
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                _preferences.CustomFfmpegDirectory = value;
                InvalidateValidateFfmpeg();
            }
        }
    }

    public bool IsFfmpegAvailable => _ffmpegService.IsAvailable;
    public string FfmpegStatusText => _ffmpegService.IsAvailable
        ? LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegReady", _ffmpegService.ResolvedDirectory ?? "", _ffmpegService.VersionInfo ?? "9.0")
        : _ffmpegService.LastError ?? LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegNotConfigured");
    public bool ShowFfmpegMissing => !_ffmpegService.IsAvailable;

    // Container / Codecs
    public List<string> AvailableContainers { get; } = ["MP4", "MKV", "MOV", "AVI", "GIF", "WebM"];
    public List<VideoContainer> ContainerValues { get; } = Enum.GetValues<VideoContainer>().ToList();

    public string? SelectedContainer
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsGifMode));
                OnPropertyChanged(nameof(ShowTwoPass));
                // 先更新源再定选，避免 SelectedItem 在旧 ItemsSource 上被 TwoWay 回推 null
                OnPropertyChanged(nameof(FilteredVideoCodecs));
                OnPropertyChanged(nameof(FilteredAudioCodecs));
                EnsureCodecSelectionValid();
                OnPropertyChanged(nameof(ContainerIndex));
                // 兼容旧索引绑定
                OnPropertyChanged(nameof(VideoCodecIndex));
                OnPropertyChanged(nameof(AudioCodecIndex));
                ResetPresetToCustom();
            }
        }
    } = "MP4";

    public int ContainerIndex
    {
        get => Math.Max(AvailableContainers.IndexOf(SelectedContainer ?? "MP4"), 0);
        set
        {
            var clamped = Math.Clamp(value, 0, AvailableContainers.Count - 1);
            SelectedContainer = AvailableContainers[clamped];
        }
    }

    public bool IsGifMode => SelectedContainer == "GIF";

    // Video codecs filtered
    public List<string> AllVideoCodecLabels { get; } = ["H.264 (x264)", "H.265 (x265)", "AV1 (aom)", "AV1 (SVT)", "VP8", "VP9", "MPEG-4", "GIF"];
    public List<VideoCodec> AllVideoCodecValues { get; } = Enum.GetValues<VideoCodec>().ToList();

    public List<string> FilteredVideoCodecs
    {
        get
        {
            var container = MapContainerLabelToEnum(SelectedContainer);
            return FilterVideoCodecsForContainer(container);
        }
    }

    public List<string> FilteredAudioCodecs
    {
        get
        {
            var container = MapContainerLabelToEnum(SelectedContainer);
            return FilterAudioCodecsForContainer(container);
        }
    }

    private static VideoContainer MapContainerLabelToEnum(string? label) => label switch
    {
        "MP4" => VideoContainer.Mp4,
        "MKV" => VideoContainer.Mkv,
        "MOV" => VideoContainer.Mov,
        "AVI" => VideoContainer.Avi,
        "GIF" => VideoContainer.Gif,
        "WebM" => VideoContainer.WebM,
        _ => VideoContainer.Mp4
    };

    /// <summary>按值校验：有交集保留（归一到新集合实例），无交集回落首项，避免 ComboBox 空选。</summary>
    private void EnsureCodecSelectionValid()
    {
        var vList = FilteredVideoCodecs;
        var aList = FilteredAudioCodecs;
        if (SelectedVideoCodec is null || !vList.Contains(SelectedVideoCodec))
            SelectedVideoCodec = vList.Count > 0 ? vList[0] : null;
        else
        {
            var idx = vList.IndexOf(SelectedVideoCodec);
            if (idx >= 0 && !ReferenceEquals(vList[idx], SelectedVideoCodec))
                SelectedVideoCodec = vList[idx];
        }
        if (SelectedAudioCodec is null || !aList.Contains(SelectedAudioCodec))
            SelectedAudioCodec = aList.Count > 0 ? aList[0] : null;
        else
        {
            var idx = aList.IndexOf(SelectedAudioCodec);
            if (idx >= 0 && !ReferenceEquals(aList[idx], SelectedAudioCodec))
                SelectedAudioCodec = aList[idx];
        }
    }

    [Obsolete("仅为兼容旧索引调用保留，请使用 SelectedVideoCodec/SelectedAudioCodec")]
    private void UpdateCodecIndicesForContainer() => EnsureCodecSelectionValid();

    private void ResetPresetToCustom()
    {
        if (_isApplyingPreset || _suppressPresetReset) return;
        if (HandbrakePresetIndex != (int)VideoPreset.Custom)
        {
            HandbrakePresetIndex = (int)VideoPreset.Custom;
        }
    }

    private static List<string> FilterVideoCodecsForContainer(VideoContainer c) => c switch
    {
        VideoContainer.Gif => ["GIF"],
        VideoContainer.Mp4 => ["H.264 (x264)", "H.265 (x265)", "AV1 (aom)", "AV1 (SVT)", "MPEG-4"],
        VideoContainer.Mkv => ["H.264 (x264)", "H.265 (x265)", "AV1 (aom)", "AV1 (SVT)", "VP8", "VP9", "MPEG-4"],
        VideoContainer.Mov => ["H.264 (x264)", "H.265 (x265)", "AV1 (aom)", "MPEG-4"],
        VideoContainer.Avi => ["H.264 (x264)", "MPEG-4"],
        VideoContainer.WebM => ["VP8", "VP9", "AV1 (aom)", "AV1 (SVT)"],
        _ => ["H.264 (x264)", "H.265 (x265)", "AV1 (aom)"]
    };

    private static List<string> FilterAudioCodecsForContainer(VideoContainer c) => c switch
    {
        VideoContainer.Gif => ["None"],
        VideoContainer.Mp4 => ["AAC", "MP3", "AC3", "Opus", "None"],
        VideoContainer.Mkv => ["AAC", "MP3", "Opus", "Vorbis", "FLAC", "AC3", "None"],
        VideoContainer.Mov => ["AAC", "MP3", "AC3", "None"],
        VideoContainer.Avi => ["MP3", "AAC", "AC3", "None"],
        VideoContainer.WebM => ["Opus", "Vorbis", "None"],
        _ => ["AAC", "MP3", "Opus", "None"]
    };

    public string? SelectedVideoCodec
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(VideoCodecIndex));
                ResetPresetToCustom();
            }
        }
    } = "H.264 (x264)";

    public string? SelectedAudioCodec
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(AudioCodecIndex));
                ResetPresetToCustom();
            }
        }
    } = "AAC";

    public int VideoCodecIndex => SelectedVideoCodec is null ? 0 : Math.Max(FilteredVideoCodecs.IndexOf(SelectedVideoCodec), 0);

    public int AudioCodecIndex => SelectedAudioCodec is null ? 0 : Math.Max(FilteredAudioCodecs.IndexOf(SelectedAudioCodec), 0);

    public List<string> HardwareBackendLabels { get; } = ["Software (CPU)", "NVIDIA NVENC", "Intel QSV/VAAPI", "AMD AMF/VAAPI"];
    public string? SelectedHardwareBackend
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsVaapiBackend));
                OnPropertyChanged(nameof(ShowTwoPass));
                OnPropertyChanged(nameof(HardwareBackendIndex));
            }
        }
    } = "Software (CPU)";

    public int HardwareBackendIndex
    {
        get => Math.Max(HardwareBackendLabels.IndexOf(SelectedHardwareBackend ?? "Software (CPU)"), 0);
        set
        {
            var clamped = Math.Clamp(value, 0, HardwareBackendLabels.Count - 1);
            SelectedHardwareBackend = HardwareBackendLabels[clamped];
        }
    }

    public bool IsVaapiBackend => ((HardwareBackend)HardwareBackendIndex == HardwareBackend.Intel || (HardwareBackend)HardwareBackendIndex == HardwareBackend.Amd) && !OperatingSystem.IsWindows();

    public List<string> RateControlLabels { get; } = ["CRF / Quality", "Bitrate"];
    public string? SelectedRateControl
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsCrfMode));
                OnPropertyChanged(nameof(IsBitrateMode));
                OnPropertyChanged(nameof(ShowTwoPass));
                OnPropertyChanged(nameof(RateControlIndex));
                ResetPresetToCustom();
            }
        }
    } = "CRF / Quality";

    public int RateControlIndex
    {
        get => Math.Max(RateControlLabels.IndexOf(SelectedRateControl ?? "CRF / Quality"), 0);
        set
        {
            var clamped = Math.Clamp(value, 0, RateControlLabels.Count - 1);
            SelectedRateControl = RateControlLabels[clamped];
        }
    }

    public bool IsCrfMode => SelectedRateControl == "CRF / Quality";
    public bool IsBitrateMode => SelectedRateControl == "Bitrate";
    public bool ShowTwoPass => IsBitrateMode && !IsGifMode && HardwareBackendIndex == 0;
    public bool IsTwoPassEnabled
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) ResetPresetToCustom();
        }
    } = false;

    public bool IncludeAllInFolderScan
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    public int CrfValue
    {
        get => field;
        set
        {
            if (SetProperty(ref field, Math.Clamp(value, 0, 51))) ResetPresetToCustom();
        }
    } = 23;

    public int VideoBitrateKbps
    {
        get => field;
        set
        {
            if (SetProperty(ref field, Math.Clamp(value, 100, 50000))) ResetPresetToCustom();
        }
    } = 2500;

    public int AudioBitrateKbps
    {
        get => field;
        set
        {
            if (SetProperty(ref field, Math.Clamp(value, 32, 512))) ResetPresetToCustom();
        }
    } = 128;

    public List<string> PresetLabels { get; } = ["Ultrafast", "Superfast", "Veryfast", "Faster", "Fast", "Medium", "Slow", "Slower", "Veryslow", "Placebo"];
    public string? SelectedPreset
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(PresetIndex));
                ResetPresetToCustom();
            }
        }
    } = "Medium";

    public int PresetIndex
    {
        get => Math.Max(PresetLabels.IndexOf(SelectedPreset ?? "Medium"), 0);
        set
        {
            var clamped = Math.Clamp(value, 0, PresetLabels.Count - 1);
            SelectedPreset = PresetLabels[clamped];
        }
    }

    // Output presets — order must match VideoPreset enum (for persistence)
    public List<string> HandbrakePresetLabels { get; } = [
        "Custom",
        "Fast 1080p30",
        "HQ 1080p30",
        "Fast 720p30",
        "HQ 720p30",
        "Fast 480p30",
        "GIF 480p 15fps",
        "Fast 1440p30",
        "HQ 1440p30",
        "Fast 2160p30",
        "HQ 2160p30",
        "Fast 1080p30 (x265)",
        "HQ 1080p30 (x265)",
        "Fast 720p30 (x265)",
        "HQ 720p30 (x265)",
        "Fast 480p30 (x265)",
        "Fast 1440p30 (x265)",
        "HQ 1440p30 (x265)",
        "Fast 2160p30 (x265)",
        "HQ 2160p30 (x265)"
    ];

    public string? SelectedHandbrakePreset
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HandbrakePresetIndex));
                if (value is not null && value != "Custom")
                {
                    var preset = MapHandbrakeLabelToPreset(value);
                    if (preset != VideoPreset.Custom)
                        ApplyHandbrakePreset(preset);
                }
            }
        }
    } = "Custom";

    public int HandbrakePresetIndex
    {
        get => Math.Max(HandbrakePresetLabels.IndexOf(SelectedHandbrakePreset ?? "Custom"), 0);
        set
        {
            var clamped = Math.Clamp(value, 0, HandbrakePresetLabels.Count - 1);
            SelectedHandbrakePreset = HandbrakePresetLabels[clamped];
        }
    }

    private static VideoPreset MapHandbrakeLabelToPreset(string label) => label switch
    {
        "Fast 1080p30" => VideoPreset.Fast1080p30,
        "HQ 1080p30" => VideoPreset.HQ1080p30,
        "Fast 720p30" => VideoPreset.Fast720p30,
        "HQ 720p30" => VideoPreset.HQ720p30,
        "Fast 480p30" => VideoPreset.Fast480p30,
        "GIF 480p 15fps" => VideoPreset.Gif480p,
        "Fast 1440p30" => VideoPreset.Fast1440p30,
        "HQ 1440p30" => VideoPreset.HQ1440p30,
        "Fast 2160p30" => VideoPreset.Fast2160p30,
        "HQ 2160p30" => VideoPreset.HQ2160p30,
        "Fast 1080p30 (x265)" => VideoPreset.Fast1080p30X265,
        "HQ 1080p30 (x265)" => VideoPreset.HQ1080p30X265,
        "Fast 720p30 (x265)" => VideoPreset.Fast720p30X265,
        "HQ 720p30 (x265)" => VideoPreset.HQ720p30X265,
        "Fast 480p30 (x265)" => VideoPreset.Fast480p30X265,
        "Fast 1440p30 (x265)" => VideoPreset.Fast1440p30X265,
        "HQ 1440p30 (x265)" => VideoPreset.HQ1440p30X265,
        "Fast 2160p30 (x265)" => VideoPreset.Fast2160p30X265,
        "HQ 2160p30 (x265)" => VideoPreset.HQ2160p30X265,
        _ => VideoPreset.Custom
    };

    // Filters
    public List<string> ScaleModeLabels { get; } = ["None", "Fit Within", "Exact", "Fix Width", "Fix Height"];
    public string? SelectedScaleMode
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsScaleActive));
                OnPropertyChanged(nameof(ScaleModeIndex));
                ResetPresetToCustom();
            }
        }
    } = "None";

    public int ScaleModeIndex
    {
        get => Math.Max(ScaleModeLabels.IndexOf(SelectedScaleMode ?? "None"), 0);
        set
        {
            var clamped = Math.Clamp(value, 0, ScaleModeLabels.Count - 1);
            SelectedScaleMode = ScaleModeLabels[clamped];
        }
    }

    public bool IsScaleActive => SelectedScaleMode != "None";

    public int ScaleWidth
    {
        get => field;
        set
        {
            if (SetProperty(ref field, Math.Clamp(value, 16, 8192))) ResetPresetToCustom();
        }
    } = 1920;

    public int ScaleHeight
    {
        get => field;
        set
        {
            if (SetProperty(ref field, Math.Clamp(value, 16, 8192))) ResetPresetToCustom();
        }
    } = 1080;

    public bool KeepAspect
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) ResetPresetToCustom();
        }
    } = true;

    public bool CropEnabled
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) ResetPresetToCustom();
        }
    }

    public int CropTop { get => field; set { if (SetProperty(ref field, Math.Clamp(value, 0, 1000))) ResetPresetToCustom(); } } = 0;
    public int CropBottom { get => field; set { if (SetProperty(ref field, Math.Clamp(value, 0, 1000))) ResetPresetToCustom(); } } = 0;
    public int CropLeft { get => field; set { if (SetProperty(ref field, Math.Clamp(value, 0, 1000))) ResetPresetToCustom(); } } = 0;
    public int CropRight { get => field; set { if (SetProperty(ref field, Math.Clamp(value, 0, 1000))) ResetPresetToCustom(); } } = 0;

    public List<string> FpsModeLabels { get; } = ["Same as Source", "Fixed", "Peak (max)"];
    public string? SelectedFpsMode
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsFpsActive));
                OnPropertyChanged(nameof(FpsModeIndex));
                ResetPresetToCustom();
            }
        }
    } = "Same as Source";

    public int FpsModeIndex
    {
        get => Math.Max(FpsModeLabels.IndexOf(SelectedFpsMode ?? "Same as Source"), 0);
        set
        {
            var clamped = Math.Clamp(value, 0, FpsModeLabels.Count - 1);
            SelectedFpsMode = FpsModeLabels[clamped];
        }
    }
    public bool IsFpsActive => SelectedFpsMode != "Same as Source";
    public double FpsValue
    {
        get => field;
        set
        {
            if (SetProperty(ref field, Math.Clamp(value, 1, 120))) ResetPresetToCustom();
        }
    } = 30;

    public List<string> DeinterlaceLabels { get; } = ["None", "Yadif", "Bwdif"];
    public string? SelectedDeinterlace
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(DeinterlaceIndex));
                ResetPresetToCustom();
            }
        }
    } = "None";

    public int DeinterlaceIndex
    {
        get => Math.Max(DeinterlaceLabels.IndexOf(SelectedDeinterlace ?? "None"), 0);
        set
        {
            var clamped = Math.Clamp(value, 0, DeinterlaceLabels.Count - 1);
            SelectedDeinterlace = DeinterlaceLabels[clamped];
        }
    }

    public List<string> DenoiseLabels { get; } = ["None", "Light (hqdn3d)", "Medium", "Strong"];
    public string? SelectedDenoise
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(DenoiseIndex));
                ResetPresetToCustom();
            }
        }
    } = "None";

    public int DenoiseIndex
    {
        get => Math.Max(DenoiseLabels.IndexOf(SelectedDenoise ?? "None"), 0);
        set
        {
            var clamped = Math.Clamp(value, 0, DenoiseLabels.Count - 1);
            SelectedDenoise = DenoiseLabels[clamped];
        }
    }

    // GIF specific
    public int GifWidth
    {
        get => field;
        set
        {
            if (SetProperty(ref field, Math.Clamp(value, 16, 1920))) ResetPresetToCustom();
        }
    } = 480;
    public int GifFps
    {
        get => field;
        set
        {
            if (SetProperty(ref field, Math.Clamp(value, 1, 30))) ResetPresetToCustom();
        }
    } = 15;
    public int GifMaxColors
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value is 128 or 256 ? value : 256)) ResetPresetToCustom();
        }
    } = 256;
    public List<int> AvailableGifMaxColors { get; } = [128, 256];
    public int GifMaxColorsIndex
    {
        get => GifMaxColors == 128 ? 0 : 1;
        set => GifMaxColors = value == 0 ? 128 : 256;
    }
    public List<string> GifDitherLabels { get; } = ["None", "Bayer", "Floyd-Steinberg", "Sierra2_4a"];
    public string? SelectedGifDither
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(GifDitherIndex));
                ResetPresetToCustom();
            }
        }
    } = "Bayer";

    public int GifDitherIndex
    {
        get => Math.Max(GifDitherLabels.IndexOf(SelectedGifDither ?? "Bayer"), 0);
        set
        {
            var clamped = Math.Clamp(value, 0, GifDitherLabels.Count - 1);
            SelectedGifDither = GifDitherLabels[clamped];
        }
    }
    public int GifLoop
    {
        get => field;
        set
        {
            if (SetProperty(ref field, Math.Clamp(value, 0, 100))) ResetPresetToCustom();
        }
    } = 0;
    public List<string> GifStatsLabels { get; } = ["diff", "single"];
    public string? SelectedGifStats
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(GifStatsIndex));
                ResetPresetToCustom();
            }
        }
    } = "diff";

    public int GifStatsIndex
    {
        get => Math.Max(GifStatsLabels.IndexOf(SelectedGifStats ?? "diff"), 0);
        set
        {
            var clamped = Math.Clamp(value, 0, GifStatsLabels.Count - 1);
            SelectedGifStats = GifStatsLabels[clamped];
        }
    }

    public bool IsBusy
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                InvalidateAllCommands();
            }
        }
    }

    private void InvalidateAllCommands()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            DoInvalidateAll();
        }
        else
        {
            Dispatcher.UIThread.Post(DoInvalidateAll);
        }
        void DoInvalidateAll()
        {
            AddFilesCommand.NotifyCanExecuteChanged();
            AddFolderCommand.NotifyCanExecuteChanged();
            ClearListCommand.NotifyCanExecuteChanged();
            PickOutputFolderCommand.NotifyCanExecuteChanged();
            OpenOutputFolderCommand.NotifyCanExecuteChanged();
            StartTranscodeCommand.NotifyCanExecuteChanged();
            PickFfmpegFolderCommand.NotifyCanExecuteChanged();
            ValidateFfmpegCommand.NotifyCanExecuteChanged();
        }
    }

    private void InvalidateCanOpenOutputFolder()
    {
        if (Dispatcher.UIThread.CheckAccess()) OpenOutputFolderCommand.NotifyCanExecuteChanged();
        else Dispatcher.UIThread.Post(() => OpenOutputFolderCommand.NotifyCanExecuteChanged());
    }

    private void InvalidateValidateFfmpeg()
    {
        if (Dispatcher.UIThread.CheckAccess()) ValidateFfmpegCommand.NotifyCanExecuteChanged();
        else Dispatcher.UIThread.Post(() => ValidateFfmpegCommand.NotifyCanExecuteChanged());
    }

    private void InvalidateStartTranscode()
    {
        if (Dispatcher.UIThread.CheckAccess()) StartTranscodeCommand.NotifyCanExecuteChanged();
        else Dispatcher.UIThread.Post(() => StartTranscodeCommand.NotifyCanExecuteChanged());
    }

    public int TotalCount { get => field; set => SetProperty(ref field, value); }
    public int CompletedCount { get => field; set => SetProperty(ref field, value); }
    public int FailedCount { get => field; set => SetProperty(ref field, value); }
    public double ConversionProgress { get => field; set => SetProperty(ref field, value); }
    public string StatusText { get => field; set => SetProperty(ref field, value); } = string.Empty;

    public VideoTranscodeViewModel(
        IVideoTranscodeService videoService,
        IFfmpegService ffmpegService,
        IFileDialogService fileDialogService,
        INotificationService notificationService,
        IViewStateService viewStateService,
        AppPreferences preferences)
    {
        _videoService = videoService;
        _ffmpegService = ffmpegService;
        _fileDialogService = fileDialogService;
        _notificationService = notificationService;
        _viewStateService = viewStateService;
        _preferences = preferences;
        _viewStateService.Register(this);
        FileItems.CollectionChanged += (_, _) => InvalidateStartTranscode();
        FfmpegDirectory = _preferences.CustomFfmpegDirectory ?? AppPaths.FfmpegBringYourOwnDirectory;
        _ffmpegService.AvailabilityChanged += OnFfmpegAvailabilityChanged;
        I18nManager.Instance.CultureChanged += OnCultureChanged;
        UpdateStatusText();
    }

    private void OnFfmpegAvailabilityChanged(object? sender, bool available)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(IsFfmpegAvailable));
            OnPropertyChanged(nameof(FfmpegStatusText));
            OnPropertyChanged(nameof(ShowFfmpegMissing));
            InvalidateStartTranscode();
        });
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateStatusText();
            OnPropertyChanged(nameof(FfmpegStatusText));
            foreach (var item in FileItems) item.RefreshLocalization();
            // Re-validate to regenerate LastError in new language
            _ = _ffmpegService.ValidateAsync();
        });
    }

    private void ApplyHandbrakePreset(VideoPreset preset)
    {
        _isApplyingPreset = true;
        try
        {
            bool isHw = SelectedHardwareBackend != "Software (CPU)";
            void ApplyFast(int w, int h, string codecLabel)
            {
                // HW presets default to HEVC (265) as requested
                string effectiveCodec = isHw && codecLabel == "H.264 (x264)" ? "H.265 (x265)" : codecLabel;
                SelectedContainer = "MP4";
                SetCodecByLabel(effectiveCodec);
                SelectedRateControl = "CRF / Quality"; CrfValue = 22; SelectedPreset = "Fast";
                SelectedScaleMode = "Fit Within"; ScaleWidth = w; ScaleHeight = h; KeepAspect = true;
                SelectedFpsMode = "Same as Source"; SelectedDeinterlace = "None"; SelectedDenoise = "None";
                SetAudioCodecByLabel("AAC");
            }
            void ApplyHQ(int w, int h, string codecLabel)
            {
                string effectiveCodec = isHw && codecLabel == "H.264 (x264)" ? "H.265 (x265)" : codecLabel;
                SelectedContainer = "MP4";
                SetCodecByLabel(effectiveCodec);
                SelectedRateControl = "CRF / Quality"; CrfValue = 20; SelectedPreset = "Slow";
                SelectedScaleMode = "Fit Within"; ScaleWidth = w; ScaleHeight = h; KeepAspect = true;
                SelectedFpsMode = "Same as Source"; SelectedDeinterlace = "Yadif"; SelectedDenoise = "None";
            }

            switch (preset)
            {
                case VideoPreset.Fast1080p30: ApplyFast(1920, 1080, "H.264 (x264)"); break;
                case VideoPreset.HQ1080p30: ApplyHQ(1920, 1080, "H.264 (x264)"); break;
                case VideoPreset.Fast720p30: ApplyFast(1280, 720, "H.264 (x264)"); break;
                case VideoPreset.HQ720p30: ApplyHQ(1280, 720, "H.264 (x264)"); break;
                case VideoPreset.Fast480p30: ApplyFast(720, 480, "H.264 (x264)"); break;
                case VideoPreset.Fast1440p30: ApplyFast(2560, 1440, "H.264 (x264)"); break;
                case VideoPreset.HQ1440p30: ApplyHQ(2560, 1440, "H.264 (x264)"); break;
                case VideoPreset.Fast2160p30: ApplyFast(3840, 2160, "H.264 (x264)"); break;
                case VideoPreset.HQ2160p30: ApplyHQ(3840, 2160, "H.264 (x264)"); break;
                case VideoPreset.Fast1080p30X265: ApplyFast(1920, 1080, "H.265 (x265)"); break;
                case VideoPreset.HQ1080p30X265: ApplyHQ(1920, 1080, "H.265 (x265)"); break;
                case VideoPreset.Fast720p30X265: ApplyFast(1280, 720, "H.265 (x265)"); break;
                case VideoPreset.HQ720p30X265: ApplyHQ(1280, 720, "H.265 (x265)"); break;
                case VideoPreset.Fast480p30X265: ApplyFast(720, 480, "H.265 (x265)"); break;
                case VideoPreset.Fast1440p30X265: ApplyFast(2560, 1440, "H.265 (x265)"); break;
                case VideoPreset.HQ1440p30X265: ApplyHQ(2560, 1440, "H.265 (x265)"); break;
                case VideoPreset.Fast2160p30X265: ApplyFast(3840, 2160, "H.265 (x265)"); break;
                case VideoPreset.HQ2160p30X265: ApplyHQ(3840, 2160, "H.265 (x265)"); break;
                case VideoPreset.Gif480p:
                    SelectedContainer = "GIF";
                    SelectedScaleMode = "None";
                    GifWidth = 480; GifFps = 15; GifMaxColors = 256; SelectedGifDither = "Bayer"; GifLoop = 0;
                    break;
            }
            OnPropertyChanged(nameof(IsGifMode));
            OnPropertyChanged(nameof(ShowTwoPass));
        }
        finally
        {
            _isApplyingPreset = false;
        }
    }

    private void SetCodecByLabel(string label)
    {
        var list = FilteredVideoCodecs;
        if (list.Contains(label)) SelectedVideoCodec = list[list.IndexOf(label)];
        else if (list.Count > 0) SelectedVideoCodec = list[0];
    }

    private void SetAudioCodecByLabel(string label)
    {
        var list = FilteredAudioCodecs;
        if (list.Contains(label)) SelectedAudioCodec = list[list.IndexOf(label)];
        else if (list.Count > 0) SelectedAudioCodec = list[0];
    }

    object IViewState.CaptureState() => new VideoTranscodeViewState
    {
        OutputFolder = OutputFolder,
        ContainerIndex = ContainerIndex,
        VideoCodecIndex = VideoCodecIndex,
        AudioCodecIndex = AudioCodecIndex,
        SelectedVideoCodec = SelectedVideoCodec,
        SelectedAudioCodec = SelectedAudioCodec,
        SelectedContainer = SelectedContainer,
        SelectedHardwareBackend = SelectedHardwareBackend,
        SelectedRateControl = SelectedRateControl,
        SelectedPreset = SelectedPreset,
        SelectedHandbrakePreset = SelectedHandbrakePreset,
        SelectedScaleMode = SelectedScaleMode,
        SelectedFpsMode = SelectedFpsMode,
        SelectedDeinterlace = SelectedDeinterlace,
        SelectedDenoise = SelectedDenoise,
        SelectedGifDither = SelectedGifDither,
        SelectedGifStats = SelectedGifStats,
        RateControlIndex = RateControlIndex,
        Crf = CrfValue,
        VideoBitrateKbps = VideoBitrateKbps,
        TwoPassEnabled = IsTwoPassEnabled,
        HardwareBackend = HardwareBackendIndex,
        AudioBitrateKbps = AudioBitrateKbps,
        PresetIndex = PresetIndex,
        ScaleModeIndex = ScaleModeIndex,
        ScaleWidth = ScaleWidth,
        ScaleHeight = ScaleHeight,
        KeepAspect = KeepAspect,
        CropEnabled = CropEnabled,
        CropTop = CropTop,
        CropBottom = CropBottom,
        CropLeft = CropLeft,
        CropRight = CropRight,
        FpsModeIndex = FpsModeIndex,
        FpsValue = FpsValue,
        DeinterlaceIndex = DeinterlaceIndex,
        DenoiseIndex = DenoiseIndex,
        GifDitherIndex = GifDitherIndex,
        GifMaxColors = GifMaxColors,
        GifFps = GifFps,
        GifWidth = GifWidth,
        GifLoop = GifLoop,
        GifStatsMode = SelectedGifStats ?? (GifStatsIndex == 0 ? "diff" : "single"),
        SelectedPresetIndex = HandbrakePresetIndex,
        IncludeAllInFolderScan = IncludeAllInFolderScan
    };

    void IViewState.RestoreState(object state)
    {
        if (state is VideoTranscodeViewState s)
        {
            _suppressPresetReset = true;
            try
            {
                OutputFolder = s.OutputFolder;
                // 容器：优先 SelectedItem，有交集保留，无则首项，兼容旧索引
                if (!string.IsNullOrEmpty(s.SelectedContainer) && AvailableContainers.Contains(s.SelectedContainer))
                    SelectedContainer = s.SelectedContainer;
                else if (!string.IsNullOrEmpty(s.SelectedContainer) && AvailableContainers.Count > 0)
                    SelectedContainer = AvailableContainers[0];
                else
                    ContainerIndex = Math.Clamp(s.ContainerIndex, 0, AvailableContainers.Count - 1);

                // 视音频编解码：基于已确定的容器过滤后，有交集保留 else 首项，归一化
                var vListForRestore = FilterVideoCodecsForContainer(MapContainerLabelToEnum(SelectedContainer));
                var aListForRestore = FilterAudioCodecsForContainer(MapContainerLabelToEnum(SelectedContainer));
                if (!string.IsNullOrEmpty(s.SelectedVideoCodec) && vListForRestore.Contains(s.SelectedVideoCodec))
                    SelectedVideoCodec = vListForRestore[vListForRestore.IndexOf(s.SelectedVideoCodec)];
                else if (!string.IsNullOrEmpty(s.SelectedVideoCodec) && vListForRestore.Count > 0)
                    SelectedVideoCodec = vListForRestore[0];
                else if (!string.IsNullOrEmpty(s.SelectedVideoCodec))
                    SelectedVideoCodec = s.SelectedVideoCodec;
                else
                {
                    int vIdx = Math.Clamp(s.VideoCodecIndex, 0, Math.Max(vListForRestore.Count - 1, 0));
                    SelectedVideoCodec = vListForRestore.Count > 0 ? vListForRestore[vIdx] : "H.264 (x264)";
                }

                if (!string.IsNullOrEmpty(s.SelectedAudioCodec) && aListForRestore.Contains(s.SelectedAudioCodec))
                    SelectedAudioCodec = aListForRestore[aListForRestore.IndexOf(s.SelectedAudioCodec)];
                else if (!string.IsNullOrEmpty(s.SelectedAudioCodec) && aListForRestore.Count > 0)
                    SelectedAudioCodec = aListForRestore[0];
                else if (!string.IsNullOrEmpty(s.SelectedAudioCodec))
                    SelectedAudioCodec = s.SelectedAudioCodec;
                else
                {
                    int aIdx = Math.Clamp(s.AudioCodecIndex, 0, Math.Max(aListForRestore.Count - 1, 0));
                    SelectedAudioCodec = aListForRestore.Count > 0 ? aListForRestore[aIdx] : "AAC";
                }
                // 再次校验，确保还原后不出现空选
                EnsureCodecSelectionValid();

                // 硬件后端
                if (!string.IsNullOrEmpty(s.SelectedHardwareBackend) && HardwareBackendLabels.Contains(s.SelectedHardwareBackend))
                    SelectedHardwareBackend = s.SelectedHardwareBackend;
                else if (!string.IsNullOrEmpty(s.SelectedHardwareBackend))
                    SelectedHardwareBackend = HardwareBackendLabels[0];
                else
                    HardwareBackendIndex = Math.Clamp(s.HardwareBackend, 0, HardwareBackendLabels.Count - 1);

                // 速率控制
                if (!string.IsNullOrEmpty(s.SelectedRateControl) && RateControlLabels.Contains(s.SelectedRateControl))
                    SelectedRateControl = s.SelectedRateControl;
                else if (!string.IsNullOrEmpty(s.SelectedRateControl))
                    SelectedRateControl = RateControlLabels[0];
                else
                    RateControlIndex = s.RateControlIndex is 0 or 1 ? s.RateControlIndex : 0;

                CrfValue = Math.Clamp(s.Crf, 0, 51);
                VideoBitrateKbps = s.VideoBitrateKbps;
                IsTwoPassEnabled = s.TwoPassEnabled;
                AudioBitrateKbps = s.AudioBitrateKbps;

                // 预设
                if (!string.IsNullOrEmpty(s.SelectedPreset) && PresetLabels.Contains(s.SelectedPreset))
                    SelectedPreset = s.SelectedPreset;
                else if (!string.IsNullOrEmpty(s.SelectedPreset))
                    SelectedPreset = PresetLabels[0];
                else
                    PresetIndex = Math.Clamp(s.PresetIndex, 0, PresetLabels.Count - 1);

                // 滤镜
                if (!string.IsNullOrEmpty(s.SelectedScaleMode) && ScaleModeLabels.Contains(s.SelectedScaleMode))
                    SelectedScaleMode = s.SelectedScaleMode;
                else if (!string.IsNullOrEmpty(s.SelectedScaleMode))
                    SelectedScaleMode = ScaleModeLabels[0];
                else
                    ScaleModeIndex = Math.Clamp(s.ScaleModeIndex, 0, ScaleModeLabels.Count - 1);

                ScaleWidth = s.ScaleWidth;
                ScaleHeight = s.ScaleHeight;
                KeepAspect = s.KeepAspect;
                CropEnabled = s.CropEnabled;
                CropTop = s.CropTop; CropBottom = s.CropBottom; CropLeft = s.CropLeft; CropRight = s.CropRight;

                if (!string.IsNullOrEmpty(s.SelectedFpsMode) && FpsModeLabels.Contains(s.SelectedFpsMode))
                    SelectedFpsMode = s.SelectedFpsMode;
                else if (!string.IsNullOrEmpty(s.SelectedFpsMode))
                    SelectedFpsMode = FpsModeLabels[0];
                else
                    FpsModeIndex = Math.Clamp(s.FpsModeIndex, 0, FpsModeLabels.Count - 1);

                FpsValue = s.FpsValue;

                if (!string.IsNullOrEmpty(s.SelectedDeinterlace) && DeinterlaceLabels.Contains(s.SelectedDeinterlace))
                    SelectedDeinterlace = s.SelectedDeinterlace;
                else if (!string.IsNullOrEmpty(s.SelectedDeinterlace))
                    SelectedDeinterlace = DeinterlaceLabels[0];
                else
                    DeinterlaceIndex = Math.Clamp(s.DeinterlaceIndex, 0, DeinterlaceLabels.Count - 1);

                if (!string.IsNullOrEmpty(s.SelectedDenoise) && DenoiseLabels.Contains(s.SelectedDenoise))
                    SelectedDenoise = s.SelectedDenoise;
                else if (!string.IsNullOrEmpty(s.SelectedDenoise))
                    SelectedDenoise = DenoiseLabels[0];
                else
                    DenoiseIndex = Math.Clamp(s.DenoiseIndex, 0, DenoiseLabels.Count - 1);

                if (!string.IsNullOrEmpty(s.SelectedGifDither) && GifDitherLabels.Contains(s.SelectedGifDither))
                    SelectedGifDither = s.SelectedGifDither;
                else if (!string.IsNullOrEmpty(s.SelectedGifDither))
                    SelectedGifDither = GifDitherLabels[0];
                else
                    GifDitherIndex = Math.Clamp(s.GifDitherIndex, 0, GifDitherLabels.Count - 1);

                GifMaxColors = s.GifMaxColors == 128 ? 128 : 256;
                GifFps = s.GifFps;
                GifWidth = s.GifWidth;
                GifLoop = s.GifLoop;

                if (!string.IsNullOrEmpty(s.SelectedGifStats) && GifStatsLabels.Contains(s.SelectedGifStats))
                    SelectedGifStats = s.SelectedGifStats;
                else if (!string.IsNullOrEmpty(s.SelectedGifStats))
                    SelectedGifStats = GifStatsLabels[0];
                else
                    GifStatsIndex = s.GifStatsMode == "single" ? 1 : 0;

                // 输出预设
                if (!string.IsNullOrEmpty(s.SelectedHandbrakePreset) && HandbrakePresetLabels.Contains(s.SelectedHandbrakePreset))
                    SelectedHandbrakePreset = s.SelectedHandbrakePreset;
                else if (!string.IsNullOrEmpty(s.SelectedHandbrakePreset))
                    SelectedHandbrakePreset = HandbrakePresetLabels[0];
                else
                    HandbrakePresetIndex = Math.Clamp(s.SelectedPresetIndex, 0, HandbrakePresetLabels.Count - 1);
                IncludeAllInFolderScan = s.IncludeAllInFolderScan;
                if (!string.IsNullOrEmpty(_preferences.CustomFfmpegDirectory)) FfmpegDirectory = _preferences.CustomFfmpegDirectory;
            }
            finally
            {
                _suppressPresetReset = false;
            }
        }
    }

[RelayCommand(CanExecute = nameof(CanModify))]
private async Task AddFiles()
{
    IReadOnlyList<FilePickerFileType> filters =
    [
        new(LocalizationRegistry.Get("VideoTranscode.Picker_VideoFile"))
        {
            Patterns =
            [
                "*.mp4", "*.mkv", "*.mov", "*.avi", "*.flv", "*.wmv", "*.m4v",
                "*.mpg", "*.mpeg", "*.3gp", "*.gif", "*.ts", "*.mts", ".m2ts", "*.webm"
            ]
        },
        new(LocalizationRegistry.Get("VideoTranscode.Picker_AllFiles")) { Patterns = ["*.*"] }
    ];
    var paths = await _fileDialogService.PickOpenFilesAsync(LocalizationRegistry.Get("VideoTranscode.Picker_SelectVideo"), filters);
    if (paths == null) return;
    var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
    foreach (string path in paths)
    {
        if (string.IsNullOrEmpty(path)) continue;
        if (!existing.Contains(path))
        {
            var item = CreateItem(path);
            FileItems.Add(item);
            existing.Add(path);
            _ = ProbeItemAsync(item);
        }
    }
    UpdateStatusText();
}

[RelayCommand(CanExecute = nameof(CanModify))]
private async Task AddFolder()
{
    string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("VideoTranscode.Picker_SelectVideoFolder"));
    if (folder == null) return;
    bool includeAll = IncludeAllInFolderScan;
    var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
    foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
    {
        bool accept = includeAll ? !string.IsNullOrEmpty(file) : IsVideoFile(file);
        if (accept && !existing.Contains(file))
        {
            var item = CreateItem(file);
            FileItems.Add(item);
            existing.Add(file);
            _ = ProbeItemAsync(item);
        }
    }
    UpdateStatusText();
}

    [RelayCommand(CanExecute = nameof(CanModify))]
    private void ClearList()
    {
        FileItems.Clear();
        UpdateStatusText();
    }

    void IVideoFileItemOwner.Remove(VideoFileItem item)
    {
        FileItems.Remove(item);
        UpdateStatusText();
    }

    private VideoFileItem CreateItem(string path)
    {
        var item = new VideoFileItem(path);
        item.Owner = this;
        return item;
    }

private async Task ProbeItemAsync(VideoFileItem item)
{
    if (!_ffmpegService.IsAvailable) return;
    try
    {
        var info = await _videoService.ProbeAsync(item.FilePath).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => item.ProbeInfo = info);
    }
    catch
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_probeFailureNotified) return;
            _probeFailureNotified = true;
            _notificationService.ShowWarn(
                LocalizationRegistry.Get("VideoTranscode.Msg_FileProbeFailed", item.FileName));
        });
    }
}

public void AddDroppedPaths(IEnumerable<string> paths)
{
    if (IsBusy) return;
    bool includeAll = IncludeAllInFolderScan;
    var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
    foreach (string path in paths)
    {
        if (string.IsNullOrEmpty(path)) continue;
        if (Directory.Exists(path))
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                bool accept = includeAll ? !string.IsNullOrEmpty(file) : IsVideoFile(file);
                if (accept && !existing.Contains(file))
                {
                    var item = CreateItem(file);
                    FileItems.Add(item);
                    existing.Add(file);
                    _ = ProbeItemAsync(item);
                }
            }
        }
        else if (File.Exists(path) && !existing.Contains(path))
        {
            var item = CreateItem(path);
            FileItems.Add(item);
            existing.Add(path);
            _ = ProbeItemAsync(item);
        }
    }
    UpdateStatusText();
}

    private bool CanModify() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task PickOutputFolder()
    {
        string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("VideoTranscode.Picker_SelectOutputDir"));
        if (folder != null) OutputFolder = folder;
    }

    [RelayCommand(CanExecute = nameof(CanOpenOutputFolder))]
    private void OpenOutputFolder()
    {
        if (!string.IsNullOrEmpty(OutputFolder))
            _fileDialogService.OpenInExplorer(OutputFolder);
    }

    private bool CanOpenOutputFolder() => !IsBusy && !string.IsNullOrEmpty(OutputFolder) && Directory.Exists(OutputFolder!);

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task PickFfmpegFolder()
    {
        string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("VideoTranscode.Picker_SelectFfmpegDir"));
        if (folder != null)
        {
            FfmpegDirectory = folder;
            _preferences.CustomFfmpegDirectory = folder;
            await _ffmpegService.ValidateAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(IsFfmpegAvailable));
                OnPropertyChanged(nameof(FfmpegStatusText));
                OnPropertyChanged(nameof(ShowFfmpegMissing));
            });
            // Re-probe all items
            foreach (var item in FileItems) _ = ProbeItemAsync(item);
        }
    }

    [RelayCommand]
    private async Task ValidateFfmpeg()
    {
        bool ok = await _ffmpegService.ValidateAsync().ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(IsFfmpegAvailable));
            OnPropertyChanged(nameof(FfmpegStatusText));
            OnPropertyChanged(nameof(ShowFfmpegMissing));
        });
        Dispatcher.UIThread.Post(() =>
        {
            if (ok) _notificationService.ShowSuccess(LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegValidated"));
            else _notificationService.ShowError(FfmpegStatusText);
        });
    }

    [RelayCommand]
    private void OpenFfmpegDownloadPage()
    {
        try
        {
            // FFmpeg exe via BtbN gpl-shared (ffmpeg.exe + ffprobe.exe). Browser handles the download; the app does not fetch or redistribute the binary.
            string url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void OpenFfmpegHelp()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://ffmpeg.org/download.html") { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand(CanExecute = nameof(CanStartTranscode))]
    private async Task StartTranscode()
    {
        if (!_ffmpegService.IsAvailable)
        {
            Dispatcher.UIThread.Post(() => _notificationService.ShowError(LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegNotAvailable")));
            return;
        }

        bool useSourceDir = string.IsNullOrWhiteSpace(OutputFolder);
        string? commonOutputDir = useSourceDir ? null : OutputFolder;

        if (!useSourceDir && !Directory.Exists(commonOutputDir!))
        {
            Dispatcher.UIThread.Post(() => _notificationService.ShowError(LocalizationRegistry.Get("VideoTranscode.Msg_OutputDirMissing")));
            return;
        }

        if (FileItems.Count == 0) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        TotalCount = FileItems.Count;
        CompletedCount = 0;
        FailedCount = 0;
        _completedCountField = 0;
        _failedCountField = 0;
        ConversionProgress = 0;
        UpdateStatusText();

        var options = BuildOptions();

        // Validate encoder availability
        string vCodecName = MapVideoCodecName(options.VideoCodec, options.HardwareBackend);
        if (!_ffmpegService.ValidateEncoder(vCodecName) && vCodecName != "gif")
        {
            // Still allow try, but warn
            _loggerField.LogWarning("Video encoder {Codec} not in available list, will still try", vCodecName);
        }

        foreach (var item in FileItems) item.Status = FileStatus.Pending;

        // Serial queue
        foreach (var item in FileItems)
        {
            if (_cts.Token.IsCancellationRequested) break;
            item.Status = FileStatus.Converting;
            item.Progress = 0;
            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => item.Progress = p));

            bool itemFailed = false;
            try
            {
                string outputDir = useSourceDir
                    ? Path.GetDirectoryName(item.FilePath) ?? Path.GetTempPath()
                    : commonOutputDir!;
                bool addOutputSuffix = useSourceDir;
                string outputPath = GetOutputPath(outputDir, item.FileName, options.Container, addOutputSuffix);
                await _videoService.TranscodeAsync(item.FilePath, outputPath, options, progress, _cts.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => { item.Progress = 1.0; item.Status = FileStatus.Completed; });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.UIThread.Post(() => item.Status = FileStatus.Pending);
                break;
            }
            catch (Exception ex)
            {
                itemFailed = true;
                string msg = ex.Message.Split('\n').FirstOrDefault() ?? ex.Message;
                Dispatcher.UIThread.Post(() => { item.Status = FileStatus.Failed; item.ErrorMessage = msg.Length > 200 ? msg[..200] : msg; });
            }
            finally
            {
                // itemFailed is set synchronously in catch; item.Status is posted async to the UI thread
                // and must NOT be read here (would race and report success despite failures).
                if (itemFailed) Interlocked.Increment(ref _failedCountField);
                int processed = Interlocked.Increment(ref _completedCountField);
                int failed = Volatile.Read(ref _failedCountField);
                Dispatcher.UIThread.Post(() =>
                {
                    CompletedCount = processed - failed;
                    FailedCount = failed;
                    ConversionProgress = (double)processed / TotalCount;
                    UpdateStatusText();
                });
            }
        }

        IsBusy = false;

        await Task.Run(() =>
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        });

        int finalFailed = Volatile.Read(ref _failedCountField);
        int finalTotal = TotalCount;
        Dispatcher.UIThread.Post(() =>
        {
            // Ensure UI counters reflect final values before notification
            FailedCount = finalFailed;
            CompletedCount = finalTotal - finalFailed;
            if (finalFailed == 0)
                _notificationService.ShowSuccess(LocalizationRegistry.Get("VideoTranscode.Msg_AllDone", finalTotal));
            else
                _notificationService.ShowWarn(LocalizationRegistry.Get("VideoTranscode.Msg_PartialFail", finalFailed, finalTotal));
        });
    }

    private bool CanStartTranscode() => !IsBusy && FileItems.Count > 0 && _ffmpegService.IsAvailable;

    private VideoTranscodeOptions BuildOptions()
    {
        var container = MapContainerLabelToEnum(SelectedContainer);
        var vCodec = MapVideoCodecByLabel(SelectedVideoCodec);
        var aCodec = MapAudioCodecByLabel(SelectedAudioCodec);

        // If Gif, force gif codec and no audio
        if (container == VideoContainer.Gif)
        {
            vCodec = VideoCodec.Gif;
            aCodec = AudioCodec.None;
        }

        return new VideoTranscodeOptions
        {
            Container = container,
            VideoCodec = vCodec,
            AudioCodec = aCodec,
            RateControl = MapRateControlByLabel(SelectedRateControl),
            Crf = CrfValue,
            VideoBitrateKbps = VideoBitrateKbps,
            TwoPassEnabled = IsTwoPassEnabled && IsBitrateMode && !IsGifMode && MapHardwareBackendByLabel(SelectedHardwareBackend) == HardwareBackend.Software,
            HardwareBackend = MapHardwareBackendByLabel(SelectedHardwareBackend),
            AudioBitrateKbps = AudioBitrateKbps,
            Preset = MapPresetByLabel(SelectedPreset),
            ScaleMode = MapScaleModeByLabel(SelectedScaleMode),
            ScaleWidth = ScaleWidth,
            ScaleHeight = ScaleHeight,
            KeepAspect = KeepAspect,
            CropEnabled = CropEnabled,
            CropTop = CropTop,
            CropBottom = CropBottom,
            CropLeft = CropLeft,
            CropRight = CropRight,
            FpsMode = MapFpsModeByLabel(SelectedFpsMode),
            FpsValue = FpsValue,
            Deinterlace = MapDeinterlaceByLabel(SelectedDeinterlace),
            Denoise = MapDenoiseByLabel(SelectedDenoise),
            GifWidth = GifWidth,
            GifFps = GifFps,
            GifMaxColors = GifMaxColors,
            GifDither = MapGifDitherByLabel(SelectedGifDither),
            GifLoop = GifLoop,
            GifStatsMode = SelectedGifStats == "single" ? "single" : "diff"
        };
    }

    private static RateControlMode MapRateControlByLabel(string? label) => label switch
    {
        "Bitrate" => RateControlMode.Bitrate,
        _ => RateControlMode.Crf
    };

    private static HardwareBackend MapHardwareBackendByLabel(string? label) => label switch
    {
        "NVIDIA NVENC" => HardwareBackend.Nvidia,
        "Intel QSV/VAAPI" => HardwareBackend.Intel,
        "AMD AMF/VAAPI" => HardwareBackend.Amd,
        _ => HardwareBackend.Software
    };

    private static PresetLevel MapPresetByLabel(string? label) => label switch
    {
        "Ultrafast" => PresetLevel.Ultrafast,
        "Superfast" => PresetLevel.Superfast,
        "Veryfast" => PresetLevel.Veryfast,
        "Faster" => PresetLevel.Faster,
        "Fast" => PresetLevel.Fast,
        "Medium" => PresetLevel.Medium,
        "Slow" => PresetLevel.Slow,
        "Slower" => PresetLevel.Slower,
        "Veryslow" => PresetLevel.Veryslow,
        "Placebo" => PresetLevel.Placebo,
        _ => PresetLevel.Medium
    };

    private static ScaleMode MapScaleModeByLabel(string? label) => label switch
    {
        "Fit Within" => ScaleMode.FitWithin,
        "Exact" => ScaleMode.Exact,
        "Fix Width" => ScaleMode.Width,
        "Fix Height" => ScaleMode.Height,
        _ => ScaleMode.None
    };

    private static FpsMode MapFpsModeByLabel(string? label) => label switch
    {
        "Fixed" => FpsMode.Fixed,
        "Peak (max)" => FpsMode.Peak,
        _ => FpsMode.SameAsSource
    };

    private static DeinterlaceMode MapDeinterlaceByLabel(string? label) => label switch
    {
        "Yadif" => DeinterlaceMode.Yadif,
        "Bwdif" => DeinterlaceMode.Bwdif,
        _ => DeinterlaceMode.None
    };

    private static DenoiseMode MapDenoiseByLabel(string? label) => label switch
    {
        "Light (hqdn3d)" => DenoiseMode.Hqdn3dLight,
        "Medium" => DenoiseMode.Hqdn3dMedium,
        "Strong" => DenoiseMode.Hqdn3dStrong,
        _ => DenoiseMode.None
    };

    private static GifDither MapGifDitherByLabel(string? label) => label switch
    {
        "Bayer" => GifDither.Bayer,
        "Floyd-Steinberg" => GifDither.FloydSteinberg,
        "Sierra2_4a" => GifDither.Sierpinski,
        _ => GifDither.None
    };

    private static VideoCodec MapFilteredVideoCodec(VideoContainer container, int filteredIndex)
    {
        var labels = FilterVideoCodecsForContainer(container);
        if (filteredIndex < 0 || filteredIndex >= labels.Count) return VideoCodec.H264;
        string label = labels[filteredIndex];
        return label switch
        {
            "H.264 (x264)" => VideoCodec.H264,
            "H.265 (x265)" => VideoCodec.H265,
            "AV1 (aom)" => VideoCodec.Av1Aom,
            "AV1 (SVT)" => VideoCodec.Av1Svt,
            "VP8" => VideoCodec.Vp8,
            "VP9" => VideoCodec.Vp9,
            "MPEG-4" => VideoCodec.Mpeg4,
            "GIF" => VideoCodec.Gif,
            _ => VideoCodec.H264
        };
    }

    private static AudioCodec MapFilteredAudioCodec(VideoContainer container, int filteredIndex)
    {
        var labels = FilterAudioCodecsForContainer(container);
        if (filteredIndex < 0 || filteredIndex >= labels.Count) return AudioCodec.Aac;
        string label = labels[filteredIndex];
        return label switch
        {
            "AAC" => AudioCodec.Aac,
            "MP3" => AudioCodec.Mp3,
            "Opus" => AudioCodec.Opus,
            "Vorbis" => AudioCodec.Vorbis,
            "FLAC" => AudioCodec.Flac,
            "AC3" => AudioCodec.Ac3,
            "None" => AudioCodec.None,
            _ => AudioCodec.Aac
        };
    }

    private static VideoCodec MapVideoCodecByLabel(string? label) => label switch
    {
        "H.264 (x264)" => VideoCodec.H264,
        "H.265 (x265)" => VideoCodec.H265,
        "AV1 (aom)" => VideoCodec.Av1Aom,
        "AV1 (SVT)" => VideoCodec.Av1Svt,
        "VP8" => VideoCodec.Vp8,
        "VP9" => VideoCodec.Vp9,
        "MPEG-4" => VideoCodec.Mpeg4,
        "GIF" => VideoCodec.Gif,
        _ => VideoCodec.H264
    };

    private static AudioCodec MapAudioCodecByLabel(string? label) => label switch
    {
        "AAC" => AudioCodec.Aac,
        "MP3" => AudioCodec.Mp3,
        "Opus" => AudioCodec.Opus,
        "Vorbis" => AudioCodec.Vorbis,
        "FLAC" => AudioCodec.Flac,
        "AC3" => AudioCodec.Ac3,
        "None" => AudioCodec.None,
        _ => AudioCodec.Aac
    };

    private static string MapVideoCodecName(VideoCodec c, HardwareBackend hw = HardwareBackend.Software)
    {
        bool isVaapi = (hw == HardwareBackend.Intel || hw == HardwareBackend.Amd) && !OperatingSystem.IsWindows();
        return (c, hw, isVaapi) switch
        {
            (VideoCodec.H264, HardwareBackend.Nvidia, _) => "h264_nvenc",
            (VideoCodec.H265, HardwareBackend.Nvidia, _) => "hevc_nvenc",
            (VideoCodec.Av1Aom, HardwareBackend.Nvidia, _) => "av1_nvenc",
            (VideoCodec.Av1Svt, HardwareBackend.Nvidia, _) => "av1_nvenc",
            (VideoCodec.H264, HardwareBackend.Intel, false) => "h264_qsv",
            (VideoCodec.H265, HardwareBackend.Intel, false) => "hevc_qsv",
            (VideoCodec.Av1Aom, HardwareBackend.Intel, false) => "av1_qsv",
            (VideoCodec.Av1Svt, HardwareBackend.Intel, false) => "av1_qsv",
            (VideoCodec.H264, HardwareBackend.Amd, false) => "h264_amf",
            (VideoCodec.H265, HardwareBackend.Amd, false) => "hevc_amf",
            (VideoCodec.Av1Aom, HardwareBackend.Amd, false) => "av1_amf",
            (VideoCodec.Av1Svt, HardwareBackend.Amd, false) => "av1_amf",
            (VideoCodec.H264, _, true) => "h264_vaapi",
            (VideoCodec.H265, _, true) => "hevc_vaapi",
            (VideoCodec.Av1Aom, _, true) => "av1_vaapi",
            (VideoCodec.Av1Svt, _, true) => "av1_vaapi",
            (VideoCodec.Vp8, _, true) => "vp8_vaapi",
            (VideoCodec.Vp9, _, true) => "vp9_vaapi",
            _ => c switch
            {
                VideoCodec.H264 => "libx264",
                VideoCodec.H265 => "libx265",
                VideoCodec.Av1Aom => "libaom-av1",
                VideoCodec.Av1Svt => "libsvtav1",
                VideoCodec.Vp8 => "libvpx",
                VideoCodec.Vp9 => "libvpx-vp9",
                VideoCodec.Mpeg4 => "mpeg4",
                VideoCodec.Gif => "gif",
                _ => "libx264"
            }
        };
    }

    private static string GetUniqueOutputPath(string folder, string fileName, VideoContainer container) => GetOutputPath(folder, fileName, container, false);

    private static string GetOutputPath(string folder, string fileName, VideoContainer container, bool addOutputSuffix)
    {
        string ext = container switch
        {
            VideoContainer.Mp4 => ".mp4",
            VideoContainer.Mkv => ".mkv",
            VideoContainer.Mov => ".mov",
            VideoContainer.Avi => ".avi",
            VideoContainer.Gif => ".gif",
            VideoContainer.WebM => ".webm",
            _ => ".mp4"
        };
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string baseName = addOutputSuffix ? stem + "_output" : stem;
        // If addOutputSuffix and baseName already ends with _output but source file is same extension, we still keep _output to avoid overwrite
        string basePath = Path.Combine(folder, baseName + ext);
        if (!File.Exists(basePath)) return basePath;
        // If file exists, append _1, _2 ... after _output (or base)
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(folder, $"{baseName}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void UpdateStatusText()
    {
        StatusText = FileItems.Count == 0
            ? LocalizationRegistry.Get("VideoTranscode.Status_Files")
            : LocalizationRegistry.Get("VideoTranscode.Status_FileCount", FileItems.Count);
    }

    private readonly ILogger<VideoTranscodeViewModel> _loggerField = LoggerFactory.Create(b => { }).CreateLogger<VideoTranscodeViewModel>();

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _ffmpegService.AvailabilityChanged -= OnFfmpegAvailabilityChanged;
        try { I18nManager.Instance.CultureChanged -= OnCultureChanged; } catch { }
        _cts?.Cancel();
        _cts?.Dispose();
        _viewStateService.Unregister(this);
    }
}
