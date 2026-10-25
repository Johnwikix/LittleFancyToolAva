using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.Services;
using FancyToolAva.Utils;

namespace FancyToolAva.ViewModels;

public partial class ImageConvertViewModel : ViewModelBase, IViewState, IFileItemOwner, IDisposable
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tiff", ".gif", ".dds", ".jxl", ".heic"
    };

    private readonly IImageConversionService _imageConversionService;
    private readonly ISuperResolutionService _superResolutionService;
    private readonly ILicenseService _licenseService;
    private readonly bool _ownsLicense;
    private bool _isDisposed;

    /// <summary>与视频转换及设置页共享的授权状态和购买命令。</summary>
    public LicenseViewModel License { get; }

    /// <summary>受限时仍允许取消已保存的勾选，以便继续普通图片转换。</summary>
    public bool CanToggleImageSr => !IsBusy && (IsSuperResolutionEnabled
        || _licenseService.CanUseFeature(LicenseFeature.ImageSuperResolution));

    /// <summary>Gets or sets whether image super-resolution prefers Intel NPU inference.</summary>
    public bool PreferSrNpu
    {
        get => _superResolutionService.PreferNpu;
        set { if (IsBusy) return; _superResolutionService.PreferNpu = value; SrDeviceText = string.Empty; OnPropertyChanged(); }
    }

    /// <summary>Gets the actual image super-resolution device and any hardware fallback notice.</summary>
    public string SrDeviceText { get => field; private set => SetProperty(ref field, value); } = string.Empty;
    private readonly IFileDialogService _fileDialogService;
    private readonly INotificationService _notificationService;
    private readonly IViewStateService _viewStateService;
    private CancellationTokenSource? _cts;
    private int _completedCountField;
    private int _failedCountField;

    string IViewState.ViewName => "imageConvertView";

    public RangeObservableCollection<ImageFileItem> FileItems { get; } = [];

    public ImageFileItem? SelectedFileItem
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
                OpenOutputFolderCommand.NotifyCanExecuteChanged();
        }
    }

    public int FormatIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsIcoMode));
                StartConvertCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsDownscaleEnabled
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value) && value && IsSuperResolutionEnabled)
            {
                _suppressSuperResolutionToggle = true;
                IsSuperResolutionEnabled = false;
                _suppressSuperResolutionToggle = false;
                OnPropertyChanged(nameof(IsDownscaleEnabled));
            }
            else
            {
                OnPropertyChanged(nameof(IsSuperResolutionEnabled));
            }
        }
    }

    public int DownscalePercent
    {
        get => field;
        set => SetProperty(ref field, value);
    } = 100;

    public int SelectedFilterIndex
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    public int SelectedSizeIndex
    {
        get => field;
        set => SetProperty(ref field, value);
    } = 2;

    public bool IsSuperResolutionEnabled
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                if (value && IsDownscaleEnabled && !_suppressSuperResolutionToggle)
                {
                    _suppressSuperResolutionToggle = true;
                    IsDownscaleEnabled = false;
                    _suppressSuperResolutionToggle = false;
                }
                OnPropertyChanged(nameof(IsDownscaleEnabled));
                OnPropertyChanged(nameof(IsSuperResolutionReady));
                OnPropertyChanged(nameof(CanToggleImageSr));
                StartConvertCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool _suppressSuperResolutionToggle;

    public int SelectedSrModelIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsSuperResolutionReady));
            }
        }
    } = 3;

    public int SelectedSrScaleIndex
    {
        get => field;
        set { if (value >= 0) SetProperty(ref field, Math.Clamp(value, 0, 1)); }
    } = 0;

    public List<int> AvailableSrScales { get; } = [2, 4];
    public List<string> AvailableSrScaleLabels { get; } = ["2x", "4x"];

    public List<string> AvailableSrModelLabels { get; } = [];

    public bool IsSuperResolutionReady =>
        IsSuperResolutionEnabled
        && SelectedSrModelIndex >= 0
        && SelectedSrModelIndex < AvailableSuperResolutionModels.Count
        && (_superResolutionService.IsModelAvailable(AvailableSuperResolutionModels[SelectedSrModelIndex])
            || IsSuperResolutionDownloading);

    public bool ShowModelMissingHint =>
        IsSuperResolutionEnabled
        && _licenseService.CanUseFeature(LicenseFeature.ImageSuperResolution)
        && !IsSuperResolutionReady
        && !IsSuperResolutionDownloading
        && string.IsNullOrEmpty(_superResolutionService.LastDownloadError);

    public bool IsSuperResolutionDownloading => _superResolutionService.IsDownloading;

    public double ModelDownloadProgress
    {
        get => field;
        private set => SetProperty(ref field, value);
    } = 0.0;

    public string ModelDownloadStatusText
    {
        get => field ?? string.Empty;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    public bool ModelDownloadHasError
    {
        get => field;
        private set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(ShowModelMissingHint));
        }
    }

    public bool IsIcoMode => FormatIndex >= 0 && FormatIndex < AvailableFormats.Count && AvailableFormats[FormatIndex] == "ico";

    public bool IsBusy
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                AddFilesCommand.NotifyCanExecuteChanged();
                AddFolderCommand.NotifyCanExecuteChanged();
                ClearListCommand.NotifyCanExecuteChanged();
                PickOutputFolderCommand.NotifyCanExecuteChanged();
                OpenOutputFolderCommand.NotifyCanExecuteChanged();
                StartConvertCommand.NotifyCanExecuteChanged();
                InstallSrAccelerationCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanToggleImageSr));
            }
        }
    }

    public int TotalCount
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    public int CompletedCount
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    public int FailedCount
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    public double ConversionProgress
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    public string StatusText
    {
        get => field;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public List<string> AvailableFormats { get; } = ["jpg", "png", "bmp", "webp", "tiff", "dds", "jxl", "heic", "ico"];
    public List<string> AvailableFilters { get; } = ["Lanczos", "Mitchell", "Catrom", "Cubic", "Triangle", "Box"];
    public List<int> AvailableSizes { get; } = [16, 32, 48, 64, 128, 256];
    public List<SuperResolutionModel> AvailableSuperResolutionModels { get; } = Enum.GetValues<SuperResolutionModel>()
        .ToList();

    public ImageConvertViewModel(
        IImageConversionService imageConversionService,
        ISuperResolutionService superResolutionService,
        IFileDialogService fileDialogService,
        INotificationService notificationService,
        IViewStateService viewStateService,
        ILicenseService? licenseService = null,
        LicenseViewModel? license = null)
    {
        _imageConversionService = imageConversionService;
        _superResolutionService = superResolutionService;
        _fileDialogService = fileDialogService;
        _notificationService = notificationService;
        _viewStateService = viewStateService;
        _licenseService = licenseService ?? license?.Service ?? LicenseService.Unrestricted;
        _ownsLicense = license == null;
        License = license ?? new LicenseViewModel(_licenseService);
        _licenseService.StateChanged += OnLicenseStateChanged;
        _viewStateService.Register(this);
        FileItems.CollectionChanged += OnFileItemsChanged;
        AvailableSrModelLabels.Add(LocalizationRegistry.Get("ImageConvert.SrModel_RealEsrganX4"));
        AvailableSrModelLabels.Add(LocalizationRegistry.Get("ImageConvert.SrModel_RealEsrganX4Anime"));
        AvailableSrModelLabels.Add(LocalizationRegistry.Get("ImageConvert.SrModel_RealEsrganGeneralX4v3"));
        AvailableSrModelLabels.Add(LocalizationRegistry.Get("ImageConvert.SrModel_SpanClearRealityV1"));
        _superResolutionService.DownloadProgressChanged += OnDownloadProgressChanged;
        _superResolutionService.IsDownloadingChanged += OnIsDownloadingChanged;
        _superResolutionService.DeviceChanged += OnDeviceChanged;
        UpdateStatusText();
    }

    private void OnFileItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => StartConvertCommand.NotifyCanExecuteChanged();

    private void OnDeviceChanged(VideoSrDevice device) => Dispatcher.UIThread.Post(() =>
    {
        if (!_isDisposed)
            SrDeviceText = device.Name + (device.FallbackReason != null ? " · " + LocalizationRegistry.Get("VideoTranscode.Sr_Fallback") : string.Empty);
    });

    private void OnLicenseStateChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_isDisposed) return;
        OnPropertyChanged(nameof(CanToggleImageSr));
        OnPropertyChanged(nameof(ShowModelMissingHint));
        StartConvertCommand.NotifyCanExecuteChanged();
        InstallSrAccelerationCommand.NotifyCanExecuteChanged();
    });

    private async Task<bool> EnsureAiAccessAsync()
    {
        try
        {
            await _licenseService.EnsureFeatureAsync(LicenseFeature.ImageSuperResolution);
            return !_isDisposed;
        }
        catch (Exception ex)
        {
            if (!_isDisposed) _notificationService.ShowWarn(ex.Message);
            return false;
        }
    }

    private void OnIsDownloadingChanged(object? sender, bool downloading) => Dispatcher.UIThread.Post(() =>
    {
        if (_isDisposed) return;
        OnPropertyChanged(nameof(IsSuperResolutionDownloading));
        OnPropertyChanged(nameof(IsSuperResolutionReady));
        OnPropertyChanged(nameof(ShowModelMissingHint));
    });

    private void OnDownloadProgressChanged(ModelDownloadProgress p) => Dispatcher.UIThread.Post(() =>
    {
        if (_isDisposed) return;
        ModelDownloadHasError = p.Stage == ModelDownloadStage.Failed;
        if (p.Stage == ModelDownloadStage.Failed)
        {
            ModelDownloadStatusText = p.Message ?? p.FileName;
        }
        else if (p.TotalBytes is long total && total > 0)
        {
            ModelDownloadProgress = Math.Clamp((double)p.BytesDownloaded / total, 0, 1);
            ModelDownloadStatusText =
                $"{p.FileName}  {(p.BytesDownloaded / 1024.0 / 1024.0):0.0} / {(total / 1024.0 / 1024.0):0.0} MB";
        }
        else
        {
            ModelDownloadStatusText = p.Message ?? p.Stage.ToString();
        }
        OnPropertyChanged(nameof(IsSuperResolutionReady));
        OnPropertyChanged(nameof(IsSuperResolutionDownloading));
        OnPropertyChanged(nameof(ShowModelMissingHint));
    });

    object IViewState.CaptureState() => new ImageConvertViewState
    {
        FormatIndex = FormatIndex,
        OutputFolder = OutputFolder,
        IsDownscaleEnabled = IsDownscaleEnabled,
        DownscalePercent = DownscalePercent,
        SelectedFilterIndex = SelectedFilterIndex,
        SelectedSizeIndex = SelectedSizeIndex,
        IsSuperResolutionEnabled = IsSuperResolutionEnabled,
        // JSON 字段沿用旧名，但保存稳定模型编号，兼容旧版本的六项索引。
        SelectedSrModelIndex = SelectedSrModelIndex >= 0 && SelectedSrModelIndex < AvailableSuperResolutionModels.Count
            ? (int)AvailableSuperResolutionModels[SelectedSrModelIndex]
            : (int)SuperResolutionModel.SpanClearRealityV1,
        SelectedSrScaleIndex = SelectedSrScaleIndex
    };

    void IViewState.RestoreState(object state)
    {
        if (state is ImageConvertViewState s)
        {
            FormatIndex = s.FormatIndex;
            OutputFolder = s.OutputFolder;
            IsDownscaleEnabled = s.IsDownscaleEnabled;
            DownscalePercent = s.DownscalePercent is > 10 and <= 100 ? s.DownscalePercent : 100;
            SelectedFilterIndex = s.SelectedFilterIndex;
            SelectedSizeIndex = s.SelectedSizeIndex;
            int modelIndex = AvailableSuperResolutionModels.IndexOf((SuperResolutionModel)s.SelectedSrModelIndex);
            SelectedSrModelIndex = modelIndex >= 0
                ? modelIndex
                : AvailableSuperResolutionModels.IndexOf(SuperResolutionModel.SpanClearRealityV1);
            SelectedSrScaleIndex = s.SelectedSrScaleIndex is 0 or 1 ? s.SelectedSrScaleIndex : 0;
            // Always restore the SR checkbox. If the model isn't ready yet (still
            // downloading or download failed), IsSuperResolutionReady / the
            // download-progress UI / ShowModelMissingHint already convey that.
            IsSuperResolutionEnabled = s.IsSuperResolutionEnabled;
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AddFiles()
    {
        IReadOnlyList<FilePickerFileType> filters = [new(LocalizationRegistry.Get("ImageConvert.Picker_ImageFile")) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tiff", "*.gif", "*.dds", "*.jxl", "*.heic"] }];
        var paths = await _fileDialogService.PickOpenFilesAsync(LocalizationRegistry.Get("ImageConvert.Picker_SelectImage"), filters);
        if (paths == null) return;
        await AddPathsAsync(paths);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AddFolder()
    {
        string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("ImageConvert.Picker_SelectImageFolder"));
        if (folder == null) return;
        await AddPathsAsync(new[] { folder });
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private void ClearList()
    {
        FileItems.Clear();
        UpdateStatusText();
    }

    void IFileItemOwner.Remove(ImageFileItem item)
    {
        FileItems.Remove(item);
        UpdateStatusText();
    }

    public void AddDroppedPaths(IEnumerable<string> paths)
    {
        if (IsBusy) return;
        _ = AddPathsAsync(paths);
    }

    /// <summary>扫描在后台线程完成（大目录枚举不能占 UI 线程），回到 UI 线程后一次性批量入列表。</summary>
    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (IsBusy) return;
        string[] roots = paths.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (roots.Length == 0) return;

        // 已有文件快照在 UI 线程构建：扫描在后台跑，期间其他添加完成会改列表，不能在后台枚举它
        var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
        var found = await Task.Run(() => BatchFileScanner.Scan(
                roots, file => ImageExtensions.Contains(Path.GetExtension(file)), existing))
            .ConfigureAwait(true);
        if (IsBusy || found.Count == 0) return;

        var current = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
        var newItems = new List<ImageFileItem>(found.Count);
        foreach ((string file, _) in found)
        {
            if (current.Contains(file)) continue;
            current.Add(file);
            newItems.Add(new ImageFileItem(file) { Owner = this });
        }
        FileItems.AddRange(newItems);
        UpdateStatusText();
    }

    private bool CanModify() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task PickOutputFolder()
    {
        string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("ImageConvert.Picker_SelectOutputDir"));
        if (folder != null)
            OutputFolder = folder;
    }

    [RelayCommand(CanExecute = nameof(CanOpenOutputFolder))]
    private void OpenOutputFolder()
    {
        if (!string.IsNullOrEmpty(OutputFolder))
            _fileDialogService.OpenInExplorer(OutputFolder);
    }

    private bool CanOpenOutputFolder()
        => !IsBusy && !string.IsNullOrEmpty(OutputFolder) && Directory.Exists(OutputFolder!);

    [RelayCommand(CanExecute = nameof(CanStartConvert))]
    private async Task StartConvert()
    {
        if (_isDisposed || IsBusy || IsHardwareInstalling) return;
        if (IsSuperResolutionEnabled && !IsIcoMode && !await EnsureAiAccessAsync()) return;
        if (_isDisposed || IsBusy || IsHardwareInstalling || FileItems.Count == 0) return;
        if (string.IsNullOrEmpty(OutputFolder))
        {
            string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("ImageConvert.Picker_SelectOutputDir"));
            if (folder == null) return;
            OutputFolder = folder;
        }

        if (_isDisposed || IsBusy || IsHardwareInstalling) return;

        if (!Directory.Exists(OutputFolder))
        {
            _notificationService.ShowError(LocalizationRegistry.Get("ImageConvert.Msg_OutputDirMissing"));
            return;
        }

        string format = AvailableFormats[FormatIndex];

        bool srEnabled = IsSuperResolutionEnabled && IsSuperResolutionReady && !IsIcoMode;
        if (IsSuperResolutionEnabled && !IsSuperResolutionReady && IsSuperResolutionDownloading)
        {
            _notificationService.ShowWarn(LocalizationRegistry.Get("ImageConvert.Msg_SrModelDownloading"));
            return;
        }
        SuperResolutionModel? srModel = srEnabled ? AvailableSuperResolutionModels[SelectedSrModelIndex] : null;
        int srScale = AvailableSrScales[SelectedSrScaleIndex];

        _cts?.Cancel();
        using var cancellation = _cts = new CancellationTokenSource();
        IsBusy = true;
        TotalCount = FileItems.Count;
        CompletedCount = 0;
        FailedCount = 0;
        _completedCountField = 0;
        _failedCountField = 0;
        ConversionProgress = 0;
        UpdateStatusText();

        // Super-resolution runs one image at a time: the DML session (and its
        // multi-GB shared-memory reservation) must never be shared across items.
        int parallelDegree = srEnabled ? 1 : Environment.ProcessorCount;

        foreach (var item in FileItems)
            item.Status = FileStatus.Pending;

        try
        {
            await Parallel.ForEachAsync(FileItems,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelDegree,
                    CancellationToken = cancellation.Token
                },
                async (item, ct) =>
                {
                    item.Status = FileStatus.Converting;
                    item.Progress = 0;
                    var progress = new ThrottledProgress<double>(p =>
                    {
                        item.Progress = p;
                    }, TimeSpan.FromMilliseconds(50));
                    try
                    {
                        if (format == "ico")
                        {
                            int size = AvailableSizes[SelectedSizeIndex];
                            string outputPath = GetUniqueOutputPath(OutputFolder!, item.FileName, "ico");
                            await _imageConversionService.SaveAsIcoAsync(item.FilePath, outputPath, size, ct);
                        }
                        else
                        {
                            string? filter = IsDownscaleEnabled ? AvailableFilters[SelectedFilterIndex] : null;
                            int? scalePct = IsDownscaleEnabled && DownscalePercent < 100 ? DownscalePercent : null;
                            string outputPath = GetUniqueOutputPath(OutputFolder!, item.FileName, format);
                            await _imageConversionService.ConvertImageFormatAsync(
                                item.FilePath,
                                outputPath,
                                format,
                                ct,
                                null,
                                filter,
                                progress,
                                scalePercent: scalePct,
                                superResolutionModel: srModel,
                                superResolutionScale: srScale,
                                superResolutionService: srEnabled ? _superResolutionService : null);
                        }
                        item.Progress = 1.0;
                        item.Status = FileStatus.Completed;
                    }
                    catch (SuperResolutionOutputTooLargeException ex)
                    {
                        item.Status = FileStatus.Failed;
                        item.ErrorMessage = LocalizationRegistry.Get(
                            "ImageConvert.Msg_SrTooLarge", ex.OutputWidth, ex.OutputHeight, ex.MaxDimension);
                    }
                    catch (OperationCanceledException)
                    {
                        item.Status = FileStatus.Pending;
                    }
                    catch (Exception ex)
                    {
                        item.Status = FileStatus.Failed;
                        item.ErrorMessage = ex.Message;
                    }
                    finally
                    {
                        bool isFailed = item.Status == FileStatus.Failed;
                        if (isFailed)
                            Interlocked.Increment(ref _failedCountField);
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
                });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 导航离开时取消队列，等待真实转换收尾后再归还令牌资源。
        }
        finally
        {
            if (ReferenceEquals(_cts, cancellation)) _cts = null;
            IsBusy = false;
        }

        // Return GPU/GL resources: destroy ONNX/DML sessions so their shared-memory
        // reservations are released back to the OS when the batch is done.
        // ReleaseSessions 持推理锁并同步 Dispose 原生会话，移出 UI 线程执行。
        if (srEnabled)
            await Task.Run(() => _superResolutionService.ReleaseSessions());

        if (_isDisposed || cancellation.IsCancellationRequested) return;

        // Return large (multi-GB) LOH allocations to the OS after the batch work is done.
        await Task.Run(() =>
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        });

        if (FailedCount == 0)
            _notificationService.ShowSuccess(LocalizationRegistry.Get("ImageConvert.Msg_AllDone", TotalCount));
        else
            _notificationService.ShowWarn(LocalizationRegistry.Get("ImageConvert.Msg_PartialFail", FailedCount, TotalCount));
    }

    private bool CanStartConvert() => !_isDisposed && !IsBusy && !IsHardwareInstalling && FileItems.Count > 0
        && (!IsSuperResolutionEnabled || IsIcoMode || _licenseService.CanUseFeature(LicenseFeature.ImageSuperResolution));

    private static string GetUniqueOutputPath(string folder, string fileName, string extension)
    {
        string ext = "." + extension;
        string basePath = Path.Combine(folder, Path.ChangeExtension(fileName, ext));
        if (!File.Exists(basePath))
            return basePath;

        string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(folder, $"{nameWithoutExt}_{i}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private void UpdateStatusText()
    {
        StatusText = FileItems.Count == 0
            ? LocalizationRegistry.Get("ImageConvert.Status_Files")
            : LocalizationRegistry.Get("ImageConvert.Status_FileCount", FileItems.Count);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        // 仅发出取消，不在 UI 线程等待持有原生会话的批处理。
        _cts?.Cancel();
        _hardwareInstallCts?.Cancel();
        _licenseService.StateChanged -= OnLicenseStateChanged;
        _superResolutionService.DownloadProgressChanged -= OnDownloadProgressChanged;
        _superResolutionService.IsDownloadingChanged -= OnIsDownloadingChanged;
        _superResolutionService.DeviceChanged -= OnDeviceChanged;
        FileItems.CollectionChanged -= OnFileItemsChanged;
        _viewStateService.Unregister(this);
        if (_ownsLicense) License.Dispose();
    }
}
