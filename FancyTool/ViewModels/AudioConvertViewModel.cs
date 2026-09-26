using System.Collections.ObjectModel;
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

public partial class AudioConvertViewModel : ViewModelBase, IViewState, IAudioFileItemOwner, IDisposable
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // 解码端全走内置 FFmpeg，输入覆盖常见（含 DSD/WavPack/APE/MPC/TTA/TAK/AMR/DTS 等）
        ".mp3", ".wav", ".flac", ".ogg", ".oga", ".m4a", ".m4b", ".aac", ".opus", ".wma",
        ".aiff", ".aif", ".ape", ".dsf", ".dff", ".wv", ".mpc", ".mp+", ".tta", ".tak",
        ".amr", ".awb", ".mka", ".caf", ".ac3", ".eac3", ".dts", ".dtshd", ".mp2", ".mid", ".aifc"
    };

    private static bool IsAudioFile(string path)
    {
        return !string.IsNullOrEmpty(path) && AudioExtensions.Contains(Path.GetExtension(path));
    }

    private readonly IAudioConvertService _convertService;
    private readonly IFfmpegService _ffmpegService;
    private readonly IFileDialogService _fileDialogService;
    private readonly INotificationService _notificationService;
    private readonly IViewStateService _viewStateService;
    private readonly ILogger<AudioConvertViewModel> _logger = LoggerFactory.Create(b => { }).CreateLogger<AudioConvertViewModel>();
    private CancellationTokenSource? _cts;
    private int _completedCountField;
    private int _failedCountField;
    private bool _isDisposed;
    private bool _probeFailureNotified;

    string IViewState.ViewName => "audioConvertView";

    public ObservableCollection<AudioFileItem> FileItems { get; } = [];

    public AudioFileItem? SelectedFileItem
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
                InvalidateCommands();
                OnPropertyChanged(nameof(IsOutputFolderEmpty));
            }
        }
    }

    public bool IsOutputFolderEmpty => string.IsNullOrWhiteSpace(OutputFolder);

    // ── 格式 ──
    public List<string> FormatLabels { get; } =
        ["MP3", "AAC (M4A)", "FLAC", "ALAC (M4A)", "WAV", "AIFF", "OGG Vorbis", "Opus", "WMA", "AC3", "MP2"];

    private static readonly AudioConvertFormat[] FormatValues =
    [
        AudioConvertFormat.Mp3, AudioConvertFormat.AacM4a, AudioConvertFormat.Flac, AudioConvertFormat.AlacM4a,
        AudioConvertFormat.Wav, AudioConvertFormat.Aiff, AudioConvertFormat.OggVorbis, AudioConvertFormat.Opus,
        AudioConvertFormat.Wma, AudioConvertFormat.Ac3, AudioConvertFormat.Mp2,
    ];

    public string? SelectedFormat
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsLosslessFormat));
                OnPropertyChanged(nameof(IsBitDepthRelevant));
            }
        }
    } = "MP3";

    public int FormatIndex
    {
        get => Math.Max(FormatLabels.IndexOf(SelectedFormat ?? "MP3"), 0);
        set => SelectedFormat = FormatLabels[Math.Clamp(value, 0, FormatLabels.Count - 1)];
    }

    public AudioConvertFormat FormatEnum => FormatValues[Math.Clamp(FormatLabels.IndexOf(SelectedFormat ?? "MP3"), 0, FormatValues.Length - 1)];

    public bool IsLosslessFormat => FormatEnum is AudioConvertFormat.Flac or AudioConvertFormat.AlacM4a
        or AudioConvertFormat.Wav or AudioConvertFormat.Aiff;
    public bool IsBitDepthRelevant => IsLosslessFormat;

    // ── 码率（有损） ──
    public int BitrateKbps { get => field; set => SetProperty(ref field, Math.Clamp(value, 16, 1000)); } = 320;

    // ── 位深（无损 PCM） ──
    public List<string> BitDepthLabels { get; } = ["Auto", "16-bit", "24-bit", "32-bit int", "32-bit float"];

    public string? SelectedBitDepth
    {
        get => field;
        set => SetProperty(ref field, value);
    } = "Auto";

    public AudioBitDepthMode BitDepthEnum => SelectedBitDepth switch
    {
        "16-bit" => AudioBitDepthMode.S16,
        "24-bit" => AudioBitDepthMode.S24,
        "32-bit int" => AudioBitDepthMode.S32,
        "32-bit float" => AudioBitDepthMode.F32,
        _ => AudioBitDepthMode.Auto,
    };

    // ── 采样率 ──
    public List<string> SampleRateLabels { get; } =
        ["Same as Source", "44100", "48000", "88200", "96000", "176400", "192000"];

    public string? SelectedSampleRate
    {
        get => field;
        set => SetProperty(ref field, value);
    } = "Same as Source";

    public int SampleRateHz
    {
        get
        {
            if (int.TryParse(SelectedSampleRate, out int hz) && hz > 0) return hz;
            return 0;
        }
    }

    // ── 增益 / 元数据 ──
    public double GainDb { get => field; set => SetProperty(ref field, Math.Clamp(value, -30, 30)); } = 0;
    public bool CopyMetadata { get => field; set => SetProperty(ref field, value); } = true;

    public bool IsFfmpegAvailable => _ffmpegService.IsAvailable;

    public bool IsBusy
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) InvalidateCommands();
        }
    }

    public int TotalCount { get => field; set => SetProperty(ref field, value); }
    public int CompletedCount { get => field; set => SetProperty(ref field, value); }
    public int FailedCount { get => field; set => SetProperty(ref field, value); }
    public double ConversionProgress { get => field; set => SetProperty(ref field, value); }
    public string StatusText { get => field; set => SetProperty(ref field, value); } = string.Empty;

    public bool IncludeAllInFolderScan { get => field; set => SetProperty(ref field, value); }

    public AudioConvertViewModel(
        IAudioConvertService convertService,
        IFfmpegService ffmpegService,
        IFileDialogService fileDialogService,
        INotificationService notificationService,
        IViewStateService viewStateService)
    {
        _convertService = convertService;
        _ffmpegService = ffmpegService;
        _fileDialogService = fileDialogService;
        _notificationService = notificationService;
        _viewStateService = viewStateService;
        _viewStateService.Register(this);
        FileItems.CollectionChanged += (_, _) => InvalidateCommands();
        I18nManager.Instance.CultureChanged += OnCultureChanged;
        UpdateStatusText();
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateStatusText();
            foreach (var item in FileItems) item.RefreshLocalization();
        });
    }

    private void InvalidateCommands()
    {
        if (Dispatcher.UIThread.CheckAccess()) DoInvalidate();
        else Dispatcher.UIThread.Post(DoInvalidate);
        void DoInvalidate()
        {
            AddFilesCommand.NotifyCanExecuteChanged();
            AddFolderCommand.NotifyCanExecuteChanged();
            ClearListCommand.NotifyCanExecuteChanged();
            PickOutputFolderCommand.NotifyCanExecuteChanged();
            OpenOutputFolderCommand.NotifyCanExecuteChanged();
            StartConvertCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanModify() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AddFiles()
    {
        IReadOnlyList<FilePickerFileType> filters =
        [
            new(LocalizationRegistry.Get("AudioTranscode.Picker_AudioFile"))
            {
                Patterns = AudioExtensions.Select(e => "*" + e).ToArray()
            },
            new(LocalizationRegistry.Get("VideoTranscode.Picker_AllFiles")) { Patterns = ["*.*"] }
        ];
        var paths = await _fileDialogService.PickOpenFilesAsync(LocalizationRegistry.Get("AudioTranscode.Picker_SelectAudio"), filters);
        if (paths == null) return;
        AddPaths(paths);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AddFolder()
    {
        string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("AudioTranscode.Picker_SelectFolder"));
        if (folder == null) return;
        AddPaths(new[] { folder });
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private void ClearList()
    {
        FileItems.Clear();
        UpdateStatusText();
    }

    void IAudioFileItemOwner.Remove(AudioFileItem item)
    {
        FileItems.Remove(item);
        UpdateStatusText();
    }

    public void AddDroppedPaths(IEnumerable<string> paths)
    {
        if (IsBusy) return;
        AddPaths(paths);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        bool includeAll = IncludeAllInFolderScan;
        var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    bool accept = includeAll ? !string.IsNullOrEmpty(file) : IsAudioFile(file);
                    if (accept && !existing.Contains(file)) AddItem(file, existing);
                }
            }
            else if (File.Exists(path) && !existing.Contains(path))
            {
                AddItem(path, existing);
            }
        }
        UpdateStatusText();
    }

    private void AddItem(string file, HashSet<string> existing)
    {
        var item = new AudioFileItem(file) { Owner = this };
        FileItems.Add(item);
        existing.Add(file);
        _ = ProbeItemAsync(item);
    }

    private async Task ProbeItemAsync(AudioFileItem item)
    {
        if (!_ffmpegService.IsAvailable) return;
        try
        {
            var info = await _convertService.ProbeAsync(item.FilePath).ConfigureAwait(false);
            if (info != null) Dispatcher.UIThread.Post(() => item.ProbeInfo = info);
        }
        catch
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_probeFailureNotified) return;
                _probeFailureNotified = true;
                _notificationService.ShowWarn(LocalizationRegistry.Get("AudioTranscode.Msg_FileProbeFailed", item.FileName));
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task PickOutputFolder()
    {
        string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("AudioTranscode.Picker_SelectOutputDir"));
        if (folder != null) OutputFolder = folder;
    }

    [RelayCommand(CanExecute = nameof(CanOpenOutputFolder))]
    private void OpenOutputFolder()
    {
        if (!string.IsNullOrEmpty(OutputFolder))
            _fileDialogService.OpenInExplorer(OutputFolder);
    }

    private bool CanOpenOutputFolder() => !IsBusy && !string.IsNullOrEmpty(OutputFolder) && Directory.Exists(OutputFolder!);

    private bool CanStartConvert() => !IsBusy && FileItems.Count > 0 && _ffmpegService.IsAvailable;

    [RelayCommand(CanExecute = nameof(CanStartConvert))]
    private async Task StartConvert()
    {
        if (!_ffmpegService.IsAvailable)
        {
            Dispatcher.UIThread.Post(() => _notificationService.ShowError(LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegNotAvailable")));
            return;
        }

        bool useSourceDir = string.IsNullOrWhiteSpace(OutputFolder);
        if (!useSourceDir && !Directory.Exists(OutputFolder!))
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

        var options = new AudioConvertOptions
        {
            Format = FormatEnum,
            BitrateKbps = BitrateKbps,
            BitDepth = BitDepthEnum,
            SampleRateHz = SampleRateHz,
            GainDb = GainDb,
            CopyMetadata = CopyMetadata,
        };
        string targetExt = AudioConvertService.GetExtension(options.Format);

        foreach (var item in FileItems) item.Status = FileStatus.Pending;

        foreach (var item in FileItems)
        {
            if (_cts.Token.IsCancellationRequested) break;
            item.Status = FileStatus.Converting;
            item.Progress = 0;
            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => item.Progress = p));

            bool itemFailed = false;
            try
            {
                string sourceDir = Path.GetDirectoryName(item.FilePath) ?? Path.GetTempPath();
                string outputDir = useSourceDir ? sourceDir : OutputFolder!;
                bool sameDir = string.Equals(Path.GetFullPath(outputDir), Path.GetFullPath(sourceDir), StringComparison.OrdinalIgnoreCase);
                string outputPath = GetOutputPath(outputDir, Path.GetFileNameWithoutExtension(item.FileName), targetExt, sameDir);
                await _convertService.ConvertAsync(item.FilePath, outputPath, options, progress, _cts.Token).ConfigureAwait(false);
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

        int finalFailed = Volatile.Read(ref _failedCountField);
        int finalTotal = TotalCount;
        Dispatcher.UIThread.Post(() =>
        {
            FailedCount = finalFailed;
            CompletedCount = finalTotal - finalFailed;
            if (finalFailed == 0)
                _notificationService.ShowSuccess(LocalizationRegistry.Get("AudioTranscode.Msg_AllDone", finalTotal));
            else
                _notificationService.ShowWarn(LocalizationRegistry.Get("VideoTranscode.Msg_PartialFail", finalFailed, finalTotal));
        });
    }

    private static string GetOutputPath(string folder, string stem, string ext, bool addSuffix)
    {
        string baseName = addSuffix ? stem + "_output" : stem;
        string basePath = Path.Combine(folder, baseName + ext);
        if (!File.Exists(basePath)) return basePath;
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(folder, $"{baseName}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void UpdateStatusText()
    {
        StatusText = FileItems.Count == 0
            ? LocalizationRegistry.Get("AudioTranscode.Status_Files")
            : LocalizationRegistry.Get("VideoTranscode.Status_FileCount", FileItems.Count);
    }

    object IViewState.CaptureState() => new AudioConvertViewState
    {
        OutputFolder = OutputFolder,
        SelectedFormat = SelectedFormat,
        FormatIndex = FormatIndex,
        SelectedBitDepth = SelectedBitDepth,
        SelectedSampleRate = SelectedSampleRate,
        BitrateKbps = BitrateKbps,
        GainDb = GainDb,
        CopyMetadata = CopyMetadata,
        IncludeAllInFolderScan = IncludeAllInFolderScan,
    };

    void IViewState.RestoreState(object state)
    {
        if (state is not AudioConvertViewState s) return;
        try
        {
            if (!string.IsNullOrEmpty(s.OutputFolder)) OutputFolder = s.OutputFolder;
            if (!string.IsNullOrEmpty(s.SelectedFormat) && FormatLabels.Contains(s.SelectedFormat))
                SelectedFormat = s.SelectedFormat;
            else
                FormatIndex = Math.Clamp(s.FormatIndex, 0, FormatLabels.Count - 1);
            if (!string.IsNullOrEmpty(s.SelectedBitDepth) && BitDepthLabels.Contains(s.SelectedBitDepth))
                SelectedBitDepth = s.SelectedBitDepth;
            if (!string.IsNullOrEmpty(s.SelectedSampleRate) && SampleRateLabels.Contains(s.SelectedSampleRate))
                SelectedSampleRate = s.SelectedSampleRate;
            BitrateKbps = Math.Clamp(s.BitrateKbps, 16, 1000);
            GainDb = Math.Clamp(s.GainDb, -30, 30);
            CopyMetadata = s.CopyMetadata;
            IncludeAllInFolderScan = s.IncludeAllInFolderScan;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restore audio-convert view state failed");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try { I18nManager.Instance.CultureChanged -= OnCultureChanged; } catch { }
        _cts?.Cancel();
        _cts?.Dispose();
        _viewStateService.Unregister(this);
    }
}
