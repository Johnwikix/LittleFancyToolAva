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
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".flv", ".wmv", ".m4v", ".mpg", ".mpeg", ".3gp", ".gif", ".ts", ".mts", ".m2ts"
    };

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
    public List<string> AvailableContainers { get; } = ["MP4", "MKV", "WebM", "MOV", "AVI", "GIF"];
    public List<VideoContainer> ContainerValues { get; } = Enum.GetValues<VideoContainer>().ToList();

    public int ContainerIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsGifMode));
                OnPropertyChanged(nameof(ShowTwoPass));
                OnPropertyChanged(nameof(FilteredVideoCodecs));
                OnPropertyChanged(nameof(FilteredAudioCodecs));
                UpdateCodecIndicesForContainer();
                ResetPresetToCustom();
            }
        }
    } = 0;

    public bool IsGifMode => ContainerIndex >= 0 && ContainerIndex < ContainerValues.Count && ContainerValues[ContainerIndex] == VideoContainer.Gif;

    // Video codecs filtered
    public List<string> AllVideoCodecLabels { get; } = ["H.264 (x264)", "H.265 (x265)", "AV1 (aom)", "AV1 (SVT)", "VP8", "VP9", "GIF"];
    public List<VideoCodec> AllVideoCodecValues { get; } = Enum.GetValues<VideoCodec>().ToList();

    public List<string> FilteredVideoCodecs
    {
        get
        {
            var container = ContainerIndex >= 0 && ContainerIndex < ContainerValues.Count ? ContainerValues[ContainerIndex] : VideoContainer.Mp4;
            return FilterVideoCodecsForContainer(container);
        }
    }

    public List<string> FilteredAudioCodecs
    {
        get
        {
            var container = ContainerIndex >= 0 && ContainerIndex < ContainerValues.Count ? ContainerValues[ContainerIndex] : VideoContainer.Mp4;
            return FilterAudioCodecsForContainer(container);
        }
    }

    private void UpdateCodecIndicesForContainer()
    {
        // Keep indices in range; if current codec not allowed, reset to 0
        if (VideoCodecIndex >= FilteredVideoCodecs.Count) VideoCodecIndex = 0;
        if (AudioCodecIndex >= FilteredAudioCodecs.Count) AudioCodecIndex = 0;
        OnPropertyChanged(nameof(FilteredVideoCodecs));
        OnPropertyChanged(nameof(FilteredAudioCodecs));
    }

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
        VideoContainer.WebM => ["VP8", "VP9", "AV1 (aom)", "AV1 (SVT)"],
        VideoContainer.Mp4 => ["H.264 (x264)", "H.265 (x265)", "VP9", "AV1 (aom)", "AV1 (SVT)"],
        VideoContainer.Mkv => ["H.264 (x264)", "H.265 (x265)", "AV1 (aom)", "AV1 (SVT)", "VP8", "VP9"],
        VideoContainer.Mov => ["H.264 (x264)", "H.265 (x265)", "AV1 (aom)"],
        VideoContainer.Avi => ["H.264 (x264)"],
        _ => ["H.264 (x264)", "H.265 (x265)", "AV1 (aom)", "VP9"]
    };

    private static List<string> FilterAudioCodecsForContainer(VideoContainer c) => c switch
    {
        VideoContainer.Gif => ["None"],
        VideoContainer.WebM => ["Opus", "Vorbis", "None"],
        VideoContainer.Mp4 => ["AAC", "MP3", "AC3", "Opus", "None"],
        VideoContainer.Mkv => ["AAC", "MP3", "Opus", "Vorbis", "FLAC", "AC3", "None"],
        VideoContainer.Mov => ["AAC", "MP3", "AC3", "None"],
        VideoContainer.Avi => ["MP3", "AAC", "AC3", "None"],
        _ => ["AAC", "MP3", "Opus", "None"]
    };

    public int VideoCodecIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) ResetPresetToCustom();
        }
    } = 0;

    public int AudioCodecIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) ResetPresetToCustom();
        }
    } = 0;

    public List<string> HardwareBackendLabels { get; } = ["Software (CPU)", "NVIDIA NVENC", "Intel QSV/VAAPI", "AMD AMF/VAAPI"];
    public int HardwareBackendIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsVaapiBackend));
                OnPropertyChanged(nameof(ShowTwoPass));
                // Hardware change does not reset preset, but two-pass visibility changes
            }
        }
    } = 0;

    public bool IsVaapiBackend => ((HardwareBackend)HardwareBackendIndex == HardwareBackend.Intel || (HardwareBackend)HardwareBackendIndex == HardwareBackend.Amd) && !OperatingSystem.IsWindows();

    public List<string> RateControlLabels { get; } = ["CRF / Quality", "Bitrate"];
    public int RateControlIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsCrfMode));
                OnPropertyChanged(nameof(IsBitrateMode));
                OnPropertyChanged(nameof(ShowTwoPass));
                ResetPresetToCustom();
            }
        }
    } = 0;

    public bool IsCrfMode => RateControlIndex == 0;
    public bool IsBitrateMode => RateControlIndex == 1;
    public bool ShowTwoPass => IsBitrateMode && !IsGifMode && HardwareBackendIndex == 0;
    public bool IsTwoPassEnabled
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) ResetPresetToCustom();
        }
    } = false;

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
    public int PresetIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) ResetPresetToCustom();
        }
    } = 5;

    // HandBrake presets — order must match VideoPreset enum (for persistence)
    public List<string> HandbrakePresetLabels { get; } = [
        "Custom",
        "Fast 1080p30",
        "HQ 1080p30",
        "Fast 720p30",
        "HQ 720p30",
        "Fast 480p30",
        "AV1 WebM 720p",
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

    public int HandbrakePresetIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                if (value != 0) ApplyHandbrakePreset((VideoPreset)value);
            }
        }
    } = 0;

    // Filters
    public List<string> ScaleModeLabels { get; } = ["None", "Fit Within", "Exact", "Fix Width", "Fix Height"];
    public int ScaleModeIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsScaleActive));
                ResetPresetToCustom();
            }
        }
    } = 0;

    public bool IsScaleActive => ScaleModeIndex != 0;

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
    public int FpsModeIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsFpsActive));
                ResetPresetToCustom();
            }
        }
    } = 0;
    public bool IsFpsActive => FpsModeIndex != 0;
    public double FpsValue
    {
        get => field;
        set
        {
            if (SetProperty(ref field, Math.Clamp(value, 1, 120))) ResetPresetToCustom();
        }
    } = 30;

    public List<string> DeinterlaceLabels { get; } = ["None", "Yadif", "Bwdif"];
    public int DeinterlaceIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) ResetPresetToCustom();
        }
    } = 0;

    public List<string> DenoiseLabels { get; } = ["None", "Light (hqdn3d)", "Medium", "Strong"];
    public int DenoiseIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) ResetPresetToCustom();
        }
    } = 0;

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
    public int GifDitherIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) ResetPresetToCustom();
        }
    } = 1;
    public int GifLoop
    {
        get => field;
        set
        {
            if (SetProperty(ref field, Math.Clamp(value, 0, 100))) ResetPresetToCustom();
        }
    } = 0;
    public List<string> GifStatsLabels { get; } = ["diff", "single"];
    public int GifStatsIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) ResetPresetToCustom();
        }
    } = 0;

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
            bool isHw = HardwareBackendIndex != 0;
            void ApplyFast(int w, int h, string codecLabel)
            {
                // HW presets default to HEVC (265) as requested
                string effectiveCodec = isHw && codecLabel == "H.264 (x264)" ? "H.265 (x265)" : codecLabel;
                ContainerIndex = 0; // MP4
                SetCodecByLabel(effectiveCodec);
                RateControlIndex = 0; CrfValue = 22; PresetIndex = 4; // Fast
                ScaleModeIndex = 1; ScaleWidth = w; ScaleHeight = h; KeepAspect = true;
                FpsModeIndex = 0; DeinterlaceIndex = 0; DenoiseIndex = 0;
                AudioCodecIndex = 0; // AAC
            }
            void ApplyHQ(int w, int h, string codecLabel)
            {
                string effectiveCodec = isHw && codecLabel == "H.264 (x264)" ? "H.265 (x265)" : codecLabel;
                ContainerIndex = 0;
                SetCodecByLabel(effectiveCodec);
                RateControlIndex = 0; CrfValue = 20; PresetIndex = 6; // Slow
                ScaleModeIndex = 1; ScaleWidth = w; ScaleHeight = h; KeepAspect = true;
                FpsModeIndex = 0; DeinterlaceIndex = 1; DenoiseIndex = 0;
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
                case VideoPreset.Av1WebM720p:
                    ContainerIndex = 2; // WebM
                    SetCodecByLabel("VP9");
                    RateControlIndex = 0; CrfValue = 32; PresetIndex = 5;
                    ScaleModeIndex = 1; ScaleWidth = 1280; ScaleHeight = 720; KeepAspect = true;
                    break;
                case VideoPreset.Gif480p:
                    ContainerIndex = 5; // GIF
                    ScaleModeIndex = 0;
                    GifWidth = 480; GifFps = 15; GifMaxColors = 256; GifDitherIndex = 1; GifLoop = 0;
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
        int idx = list.IndexOf(label);
        if (idx >= 0) VideoCodecIndex = idx;
    }

    object IViewState.CaptureState() => new VideoTranscodeViewState
    {
        OutputFolder = OutputFolder,
        ContainerIndex = ContainerIndex,
        VideoCodecIndex = VideoCodecIndex,
        AudioCodecIndex = AudioCodecIndex,
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
        GifStatsMode = GifStatsIndex == 0 ? "diff" : "single",
        SelectedPresetIndex = HandbrakePresetIndex
    };

    void IViewState.RestoreState(object state)
    {
        if (state is VideoTranscodeViewState s)
        {
            _suppressPresetReset = true;
            try
            {
                OutputFolder = s.OutputFolder;
                ContainerIndex = Math.Clamp(s.ContainerIndex, 0, AvailableContainers.Count - 1);
                VideoCodecIndex = s.VideoCodecIndex;
                AudioCodecIndex = s.AudioCodecIndex;
                RateControlIndex = s.RateControlIndex is 0 or 1 ? s.RateControlIndex : 0;
                CrfValue = Math.Clamp(s.Crf, 0, 51);
                VideoBitrateKbps = s.VideoBitrateKbps;
                IsTwoPassEnabled = s.TwoPassEnabled;
                HardwareBackendIndex = Math.Clamp(s.HardwareBackend, 0, HardwareBackendLabels.Count - 1);
                AudioBitrateKbps = s.AudioBitrateKbps;
                PresetIndex = Math.Clamp(s.PresetIndex, 0, PresetLabels.Count - 1);
                ScaleModeIndex = Math.Clamp(s.ScaleModeIndex, 0, ScaleModeLabels.Count - 1);
                ScaleWidth = s.ScaleWidth;
                ScaleHeight = s.ScaleHeight;
                KeepAspect = s.KeepAspect;
                CropEnabled = s.CropEnabled;
                CropTop = s.CropTop; CropBottom = s.CropBottom; CropLeft = s.CropLeft; CropRight = s.CropRight;
                FpsModeIndex = Math.Clamp(s.FpsModeIndex, 0, FpsModeLabels.Count - 1);
                FpsValue = s.FpsValue;
                DeinterlaceIndex = Math.Clamp(s.DeinterlaceIndex, 0, DeinterlaceLabels.Count - 1);
                DenoiseIndex = Math.Clamp(s.DenoiseIndex, 0, DenoiseLabels.Count - 1);
                GifDitherIndex = Math.Clamp(s.GifDitherIndex, 0, GifDitherLabels.Count - 1);
                GifMaxColors = s.GifMaxColors == 128 ? 128 : 256;
                GifFps = s.GifFps;
                GifWidth = s.GifWidth;
                GifLoop = s.GifLoop;
                GifStatsIndex = s.GifStatsMode == "single" ? 1 : 0;
                HandbrakePresetIndex = Math.Clamp(s.SelectedPresetIndex, 0, HandbrakePresetLabels.Count - 1);
                if (!string.IsNullOrEmpty(s.GifStatsMode)) GifStatsIndex = s.GifStatsMode == "single" ? 1 : 0;
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
        IReadOnlyList<FilePickerFileType> filters = [new(LocalizationRegistry.Get("VideoTranscode.Picker_VideoFile")) { Patterns = ["*.mp4", "*.mkv", "*.webm", "*.mov", "*.avi", "*.flv", "*.wmv", "*.m4v", "*.mpg", "*.mpeg", "*.gif"] }];
        var paths = await _fileDialogService.PickOpenFilesAsync(LocalizationRegistry.Get("VideoTranscode.Picker_SelectVideo"), filters);
        if (paths == null) return;
        var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (!existing.Contains(path) && VideoExtensions.Contains(Path.GetExtension(path)))
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
        var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (VideoExtensions.Contains(Path.GetExtension(file)) && !existing.Contains(file))
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
        catch { /* ignore probe failure */ }
    }

    public void AddDroppedPaths(IEnumerable<string> paths)
    {
        if (IsBusy) return;
        var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    if (VideoExtensions.Contains(Path.GetExtension(file)) && !existing.Contains(file))
                    {
                        var item = CreateItem(file);
                        FileItems.Add(item);
                        existing.Add(file);
                        _ = ProbeItemAsync(item);
                    }
                }
            }
            else if (File.Exists(path) && VideoExtensions.Contains(Path.GetExtension(path)) && !existing.Contains(path))
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
            // FFmpeg 9.0 native via FFmpeg.AutoGen: recommend BtbN gpl-shared latest (avcodec-63). Point to releases page for latest 9.0 build.
            string url = "https://github.com/BtbN/FFmpeg-Builds/releases";
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
        var container = ContainerIndex >= 0 && ContainerIndex < ContainerValues.Count ? ContainerValues[ContainerIndex] : VideoContainer.Mp4;
        var vCodec = MapFilteredVideoCodec(container, VideoCodecIndex);
        var aCodec = MapFilteredAudioCodec(container, AudioCodecIndex);

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
            RateControl = RateControlIndex == 0 ? RateControlMode.Crf : RateControlMode.Bitrate,
            Crf = CrfValue,
            VideoBitrateKbps = VideoBitrateKbps,
            TwoPassEnabled = IsTwoPassEnabled && IsBitrateMode && !IsGifMode && HardwareBackendIndex == 0,
            HardwareBackend = (HardwareBackend)HardwareBackendIndex,
            AudioBitrateKbps = AudioBitrateKbps,
            Preset = (PresetLevel)PresetIndex,
            ScaleMode = (ScaleMode)ScaleModeIndex,
            ScaleWidth = ScaleWidth,
            ScaleHeight = ScaleHeight,
            KeepAspect = KeepAspect,
            CropEnabled = CropEnabled,
            CropTop = CropTop,
            CropBottom = CropBottom,
            CropLeft = CropLeft,
            CropRight = CropRight,
            FpsMode = (FpsMode)FpsModeIndex,
            FpsValue = FpsValue,
            Deinterlace = (DeinterlaceMode)DeinterlaceIndex,
            Denoise = (DenoiseMode)DenoiseIndex,
            GifWidth = GifWidth,
            GifFps = GifFps,
            GifMaxColors = GifMaxColors,
            GifDither = (GifDither)GifDitherIndex,
            GifLoop = GifLoop,
            GifStatsMode = GifStatsIndex == 1 ? "single" : "diff"
        };
    }

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
            VideoContainer.WebM => ".webm",
            VideoContainer.Mov => ".mov",
            VideoContainer.Avi => ".avi",
            VideoContainer.Gif => ".gif",
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
