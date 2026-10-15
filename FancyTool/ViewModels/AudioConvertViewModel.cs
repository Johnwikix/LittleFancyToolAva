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
    private readonly ILogger<AudioConvertViewModel> _logger;
    private CancellationTokenSource? _cts;
    private int _completedCountField;
    private int _failedCountField;
    private bool _isDisposed;
    private bool _probeFailureNotified;
    private volatile bool _stopRequested;

    string IViewState.ViewName => "audioConvertView";

    public RangeObservableCollection<AudioFileItem> FileItems { get; } = [];

    // 探测队列闸门：上千文件若不限流并发开 FFmpeg 探测，线程池与磁盘会被打满
    private readonly SemaphoreSlim _probeGate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

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
                OnPropertyChanged(nameof(IsUpsamplingActive));
                OnPropertyChanged(nameof(IsAudioSrActive));
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

    public bool IsLosslessFormat => AudioConvertService.IsLosslessOutput(FormatEnum);
    /// <summary>Gets whether the saved upsampling preference applies to the currently selected output.</summary>
    public bool IsUpsamplingActive => EnableSpectralEnhancement && IsLosslessFormat;
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
    /// <summary>Gets or sets a value indicating whether spectral enhancement is enabled.</summary>
    public bool EnableSpectralEnhancement
    {
        get => field;
        set
        {
            if (!SetProperty(ref field, value)) return;
            OnPropertyChanged(nameof(IsUpsamplingActive));
            OnPropertyChanged(nameof(IsAudioSrActive));
        }
    }
    /// <summary>Gets the available target-rate multipliers.</summary>
    public IReadOnlyList<string> UpsamplingMultiplierLabels { get; } = ["1×", "2×", "4×"];
    /// <summary>Gets or sets the desired output multiplier.</summary>
    public int UpsamplingMultiplier
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value is 1 or 2 or 4 ? value : 2))
                OnPropertyChanged(nameof(UpsamplingMultiplierIndex));
        }
    } = 2;
    /// <summary>Gets or sets the multiplier selector without accepting transient cleared selections.</summary>
    public int UpsamplingMultiplierIndex
    {
        get => UpsamplingMultiplier switch { 1 => 0, 4 => 2, _ => 1 };
        set { if (value is >= 0 and <= 2) UpsamplingMultiplier = value == 0 ? 1 : value == 1 ? 2 : 4; }
    }
    /// <summary>Gets or sets the backend selected by saved or future backend-specific settings.</summary>
    public string UpsamplingBackendId
    {
        get => field;
        set
        {
            if (!SetProperty(ref field, string.IsNullOrWhiteSpace(value) ? AudioEnhancement.AudioUpsamplingBackendRegistry.DefaultBackendId : value)) return;
            OnPropertyChanged(nameof(UpsamplingBackendIndex));
            OnPropertyChanged(nameof(IsAudioSrSelected));
            OnPropertyChanged(nameof(UpsamplingInputHint));
            OnPropertyChanged(nameof(IsAudioSrActive));
        }
    } = AudioEnhancement.AudioUpsamplingBackendRegistry.DefaultBackendId;
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

    /// <summary>批量暂停中：不再启动新任务，进行中的任务跑完为止。</summary>
    public bool IsPaused
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(PauseResumeLabel));
        }
    }

    public string PauseResumeLabel => LocalizationRegistry.Get(IsPaused ? "Common.Button_Resume" : "Common.Button_Pause");

    /// <summary>批量总进度文本（百分比 + 完成数 + 失败数），转换期间显示。</summary>
    public string BatchProgressText { get => field; set => SetProperty(ref field, value); } = string.Empty;

    public bool IncludeAllInFolderScan { get => field; set => SetProperty(ref field, value); }

    public AudioConvertViewModel(
        IAudioConvertService convertService,
        IFfmpegService ffmpegService,
        IFileDialogService fileDialogService,
        INotificationService notificationService,
        IViewStateService viewStateService,
        ILogger<AudioConvertViewModel>? logger = null, AudioSrModelService? audioSrModels = null)
    {
        InitializeAudioSr(audioSrModels);
        _convertService = convertService;
        _ffmpegService = ffmpegService;
        _fileDialogService = fileDialogService;
        _notificationService = notificationService;
        _viewStateService = viewStateService;
        _logger = logger ?? LoggerFactory.Create(b => { }).CreateLogger<AudioConvertViewModel>();
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
            OnPropertyChanged(nameof(PauseResumeLabel));
            RefreshBatchProgressText();
            foreach (var option in AudioSrAccelerationLabels) option.Refresh();
            OnPropertyChanged(nameof(AudioSrInstallLabel));
            OnPropertyChanged(nameof(UpsamplingInputHint));
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
            TogglePauseCommand.NotifyCanExecuteChanged();
            StopConvertCommand.NotifyCanExecuteChanged();
            RefreshAudioSrState();
        }
    }

    private bool CanModify() => !IsBusy;

    private bool CanPauseOrStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanPauseOrStop))]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand(CanExecute = nameof(CanPauseOrStop))]
    private void StopConvert()
    {
        if (!IsBusy) return;
        _stopRequested = true;
        _logger.LogWarning("用户终止批量音频转换（已完成 {Done}/{Total}）", _completedCountField - Volatile.Read(ref _failedCountField), TotalCount);
        _cts?.Cancel();
    }

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
        await AddPathsAsync(paths);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AddFolder()
    {
        string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("AudioTranscode.Picker_SelectFolder"));
        if (folder == null) return;
        await AddPathsAsync(new[] { folder });
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
        _ = AddPathsAsync(paths);
    }

    /// <summary>
    /// 扫描与去重在后台线程完成（上万文件/大目录时枚举可达分钟级，不能占着 UI 线程），
    /// 回到 UI 线程后一次性 AddRange 批量入列表，避免逐条通知引发上千次布局。
    /// </summary>
    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (IsBusy) return;
        string[] roots = paths.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (roots.Length == 0) return;

        bool includeAll = IncludeAllInFolderScan;
        // 已有文件快照在 UI 线程构建：扫描在后台跑，期间其他添加完成会改列表，不能在后台枚举它
        var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
        var found = await Task.Run(() => BatchFileScanner.Scan(roots, file => includeAll || IsAudioFile(file), existing))
            .ConfigureAwait(true);
        if (IsBusy || found.Count == 0) return;

        // 再次按当前列表去重（扫描期间用户可能又添加了同一批文件）
        var current = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
        var newItems = new List<AudioFileItem>(found.Count);
        foreach ((string file, string? root) in found)
        {
            if (current.Contains(file)) continue;
            current.Add(file);
            newItems.Add(new AudioFileItem(file, root) { Owner = this });
        }
        FileItems.AddRange(newItems);
        foreach (var item in newItems)
            _ = ProbeItemAsync(item);
        UpdateStatusText();
    }

    private async Task ProbeItemAsync(AudioFileItem item)
    {
        if (!_ffmpegService.IsAvailable) return;
        await _probeGate.WaitAsync().ConfigureAwait(false);
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
        finally
        {
            _probeGate.Release();
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

    private bool CanStartConvert() => !IsBusy && !IsAudioSrInstalling && FileItems.Count > 0 && _ffmpegService.IsAvailable;

    [RelayCommand(CanExecute = nameof(CanStartConvert))]
    private async Task StartConvert()
    {
        if (IsBusy || IsAudioSrInstalling) return;
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
        _stopRequested = false;
        IsPaused = false;
        IsBusy = true;
        TotalCount = FileItems.Count;
        CompletedCount = 0;
        FailedCount = 0;
        _completedCountField = 0;
        _failedCountField = 0;
        ConversionProgress = 0;
        UpdateStatusText();
        RefreshBatchProgressText();

        var options = new AudioConvertOptions
        {
            Format = FormatEnum,
            BitrateKbps = BitrateKbps,
            BitDepth = BitDepthEnum,
            SampleRateHz = SampleRateHz,
            GainDb = GainDb,
            CopyMetadata = CopyMetadata,
            EnableSpectralEnhancement = EnableSpectralEnhancement,
            UpsamplingMultiplier = UpsamplingMultiplier,
            UpsamplingBackendId = UpsamplingBackendId,
            UpsamplingInference = new((VideoSrBackend)AudioSrAccelerationIndex, AudioSrAllowCpuFallback, AudioSrSteps),
        };
        string targetExt = AudioConvertService.GetExtension(options.Format);
        _logger.LogInformation("批量音频转换开始：{Count} 个文件，格式 {Format}，输出目录 {Dir}",
            TotalCount, options.Format, useSourceDir ? "(源文件目录)" : OutputFolder);

        foreach (var item in FileItems) item.Status = FileStatus.Pending;

        // 路径规划含逐文件 File.Exists/建目录磁盘 IO，上千文件时移出 UI 线程
        var snapshot = FileItems.ToArray();
        var plan = await Task.Run(() =>
        {
            // 并行执行前统一规划输出路径：文件夹添加的文件镜像源目录结构到输出目录，
            // 单独添加的文件平铺；同名文件在批内与磁盘上去重，避免并发写冲突。
            var taken = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var result = new List<(AudioFileItem, string)>(snapshot.Length);
            foreach (var item in snapshot)
            {
                string sourceDir = Path.GetDirectoryName(item.FilePath) ?? Path.GetTempPath();
                string outputDir = useSourceDir ? sourceDir
                    : OutputPathPlanner.ResolveOutputDir(OutputFolder!, item.FilePath, item.RootFolder);
                bool sameDir = string.Equals(Path.GetFullPath(outputDir), Path.GetFullPath(sourceDir), StringComparison.OrdinalIgnoreCase);
                string outputPath = OutputPathPlanner.PlanUniquePath(outputDir,
                    Path.GetFileNameWithoutExtension(item.FileName), targetExt, sameDir, taken);
                result.Add((item, outputPath));
            }
            return result;
        }).ConfigureAwait(false);

        // 单条音频管线基本单线程，并行度取逻辑核数（封顶 8 已足够吃满 IO+编码）
        int dop = options.EnableSpectralEnhancement && AudioConvertService.IsLosslessOutput(options.Format)
            && options.UpsamplingBackendId == AudioEnhancement.AudioSrUpsamplingBackend.BackendId
            ? 1 : Math.Clamp(Environment.ProcessorCount, 1, 8);
        await Parallel.ForEachAsync(plan, new ParallelOptions { MaxDegreeOfParallelism = dop }, async (entry, _) =>
        {
            var (item, outputPath) = entry;
            // 暂停：不再启动新任务（进行中的跑完为止）；终止：直接返回不再排队
            while (IsPaused && !_cts.Token.IsCancellationRequested)
                await Task.Delay(200).ConfigureAwait(false);
            if (_cts.Token.IsCancellationRequested) return;

            Dispatcher.UIThread.Post(() => { item.Status = FileStatus.Converting; item.Progress = 0; });
            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => item.Progress = p));

            bool itemFailed = false;
            try
            {
                await _convertService.ConvertAsync(item.FilePath, outputPath, options, progress, _cts.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => { item.Progress = 1.0; item.Status = FileStatus.Completed; });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.UIThread.Post(() => item.Status = FileStatus.Pending);
            }
            catch (Exception ex)
            {
                itemFailed = true;
                string msg = ex.Message.Split('\n').FirstOrDefault() ?? ex.Message;
                // 失败明细落日志文件（logs/tool-*.log）：哪个文件、因为什么
                _logger.LogError(ex, "音频转换失败: {File} -> {Output}", item.FilePath, outputPath);
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
                    RefreshBatchProgressText();
                });
            }
        }).ConfigureAwait(false);

        IsBusy = false;

        int finalFailed = Volatile.Read(ref _failedCountField);
        int finalTotal = TotalCount;
        int finalProcessed = Volatile.Read(ref _completedCountField);
        int finalDone = finalProcessed - finalFailed;
        Dispatcher.UIThread.Post(() =>
        {
            FailedCount = finalFailed;
            CompletedCount = finalDone;
            if (_stopRequested)
            {
                _logger.LogWarning("批量音频转换已终止：完成 {Done}/{Total}，失败 {Failed}", finalDone, finalTotal, finalFailed);
                _notificationService.ShowInfo(LocalizationRegistry.Get("VideoTranscode.Msg_Stopped", finalDone, finalTotal, finalFailed));
            }
            else if (finalFailed == 0)
                _notificationService.ShowSuccess(LocalizationRegistry.Get("AudioTranscode.Msg_AllDone", finalTotal));
            else
                _notificationService.ShowWarn(LocalizationRegistry.Get("VideoTranscode.Msg_PartialFail", finalFailed, finalTotal));
        });
    }

    private void RefreshBatchProgressText()
    {
        BatchProgressText = TotalCount > 0
            ? LocalizationRegistry.Get("VideoTranscode.Status_BatchProgress", CompletedCount, TotalCount, ConversionProgress, FailedCount)
            : string.Empty;
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
        EnableSpectralEnhancement = EnableSpectralEnhancement,
        UpsamplingMultiplier = UpsamplingMultiplier,
        UpsamplingBackendId = UpsamplingBackendId,
        AudioSrAccelerationIndex = AudioSrAccelerationIndex,
        AudioSrAllowCpuFallback = AudioSrAllowCpuFallback,
        AudioSrSteps = AudioSrSteps,
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
            EnableSpectralEnhancement = s.EnableSpectralEnhancement;
            UpsamplingMultiplier = s.UpsamplingMultiplier;
            UpsamplingBackendId = string.IsNullOrWhiteSpace(s.UpsamplingBackendId) ? AudioEnhancement.AudioUpsamplingBackendRegistry.DefaultBackendId : s.UpsamplingBackendId;
            AudioSrAccelerationIndex = Math.Clamp(s.AudioSrAccelerationIndex, 0, 3);
            AudioSrAllowCpuFallback = s.AudioSrAllowCpuFallback;
            AudioSrSteps = s.AudioSrSteps;
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
        DisposeAudioSr();
        try { I18nManager.Instance.CultureChanged -= OnCultureChanged; } catch { }
        _cts?.Cancel();
        _cts?.Dispose();
        _viewStateService.Unregister(this);
    }
}
