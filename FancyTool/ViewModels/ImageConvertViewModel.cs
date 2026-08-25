using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.Services;
using FancyToolAva.Utils;
using SkiaSharp;

namespace FancyToolAva.ViewModels;

public partial class ImageConvertViewModel : ViewModelBase, IViewState, IFileItemOwner
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tiff", ".gif", ".dds", ".jxl", ".heic"
    };

    private readonly IImageConversionService _imageConversionService;
    private readonly ISuperResolutionService _superResolutionService;
    private readonly IFileDialogService _fileDialogService;
    private readonly INotificationService _notificationService;
    private readonly IViewStateService _viewStateService;
    private CancellationTokenSource? _cts;
    private int _completedCountField;
    private int _failedCountField;

    string IViewState.ViewName => "imageConvertView";

    public ObservableCollection<ImageFileItem> FileItems { get; } = [];

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
                OnPropertyChanged(nameof(IsIcoMode));
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
                OnPropertyChanged(nameof(IsSuperResolutionReady));
        }
    } = 0;

    public int SelectedSrScaleIndex
    {
        get => field;
        set => SetProperty(ref field, value);
    } = 0;

    public List<int> AvailableSrScales { get; } = [2, 4];
    public List<string> AvailableSrScaleLabels { get; } = ["2x", "4x"];

    public List<string> AvailableSrModelLabels { get; } = [];

    public bool IsSuperResolutionReady =>
        IsSuperResolutionEnabled
        && SelectedSrModelIndex >= 0
        && SelectedSrModelIndex < AvailableSuperResolutionModels.Count
        && _superResolutionService.IsModelAvailable(AvailableSuperResolutionModels[SelectedSrModelIndex]);

    public bool ShowModelMissingHint => IsSuperResolutionEnabled && !IsSuperResolutionReady;

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
    public List<SuperResolutionModel> AvailableSuperResolutionModels { get; } = Enum.GetValues<SuperResolutionModel>().ToList();

    public ImageConvertViewModel(
        IImageConversionService imageConversionService,
        ISuperResolutionService superResolutionService,
        IFileDialogService fileDialogService,
        INotificationService notificationService,
        IViewStateService viewStateService)
    {
        _imageConversionService = imageConversionService;
        _superResolutionService = superResolutionService;
        _fileDialogService = fileDialogService;
        _notificationService = notificationService;
        _viewStateService = viewStateService;
        _viewStateService.Register(this);
        FileItems.CollectionChanged += (_, _) => StartConvertCommand.NotifyCanExecuteChanged();
        AvailableSrModelLabels.Add(LocalizationRegistry.Get("ImageConvert.SrModel_RealEsrganX4"));
        AvailableSrModelLabels.Add(LocalizationRegistry.Get("ImageConvert.SrModel_RealEsrganX4Anime"));
        AvailableSrModelLabels.Add(LocalizationRegistry.Get("ImageConvert.SrModel_RealEsrganGeneralX4v3"));
        UpdateStatusText();
    }

    object IViewState.CaptureState() => new ImageConvertViewState
    {
        FormatIndex = FormatIndex,
        OutputFolder = OutputFolder,
        IsDownscaleEnabled = IsDownscaleEnabled,
        DownscalePercent = DownscalePercent,
        SelectedFilterIndex = SelectedFilterIndex,
        SelectedSizeIndex = SelectedSizeIndex,
        IsSuperResolutionEnabled = IsSuperResolutionEnabled,
        SelectedSrModelIndex = SelectedSrModelIndex,
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
            SelectedSrModelIndex = s.SelectedSrModelIndex >= 0 && s.SelectedSrModelIndex < AvailableSuperResolutionModels.Count
                ? s.SelectedSrModelIndex
                : 0;
            SelectedSrScaleIndex = s.SelectedSrScaleIndex is 0 or 1 ? s.SelectedSrScaleIndex : 0;
            IsSuperResolutionEnabled = s.IsSuperResolutionEnabled && IsSuperResolutionReady;
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AddFiles()
    {
        IReadOnlyList<FilePickerFileType> filters = [new(LocalizationRegistry.Get("ImageConvert.Picker_ImageFile")) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tiff", "*.gif", "*.dds", "*.jxl", "*.heic"] }];
        var paths = await _fileDialogService.PickOpenFilesAsync(LocalizationRegistry.Get("ImageConvert.Picker_SelectImage"), filters);
        if (paths == null) return;

        var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (!existing.Contains(path))
            {
                FileItems.Add(CreateItem(path));
                existing.Add(path);
            }
        }
        UpdateStatusText();
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AddFolder()
    {
        string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("ImageConvert.Picker_SelectImageFolder"));
        if (folder == null) return;

        var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (ImageExtensions.Contains(Path.GetExtension(file)) && !existing.Contains(file))
            {
                FileItems.Add(CreateItem(file));
                existing.Add(file);
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

    void IFileItemOwner.Remove(ImageFileItem item)
    {
        FileItems.Remove(item);
        UpdateStatusText();
    }

    private ImageFileItem CreateItem(string path)
    {
        var item = new ImageFileItem(path);
        item.Owner = this;
        return item;
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
                    if (ImageExtensions.Contains(Path.GetExtension(file)) && !existing.Contains(file))
                    {
                        FileItems.Add(CreateItem(file));
                        existing.Add(file);
                    }
                }
            }
            else if (File.Exists(path) && ImageExtensions.Contains(Path.GetExtension(path)) && !existing.Contains(path))
            {
                FileItems.Add(CreateItem(path));
                existing.Add(path);
            }
        }
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
        if (string.IsNullOrEmpty(OutputFolder))
        {
            string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("ImageConvert.Picker_SelectOutputDir"));
            if (folder == null) return;
            OutputFolder = folder;
        }

        if (!Directory.Exists(OutputFolder))
        {
            _notificationService.ShowError(LocalizationRegistry.Get("ImageConvert.Msg_OutputDirMissing"));
            return;
        }

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

        string format = AvailableFormats[FormatIndex];

        bool srEnabled = IsSuperResolutionEnabled && IsSuperResolutionReady && !IsIcoMode;
        SuperResolutionModel? srModel = srEnabled ? AvailableSuperResolutionModels[SelectedSrModelIndex] : null;
        int srScale = AvailableSrScales[SelectedSrScaleIndex];

        int parallelDegree = Environment.ProcessorCount;
        if (srEnabled)
        {
            const long memoryBudget = 2L * 1024 * 1024 * 1024;
            long maxEstimate = 0;
            foreach (var item in FileItems)
            {
                try
                {
                    using var codec = SKCodec.Create(item.FilePath);
                    if (codec == null) continue;
                    long srcPx = (long)codec.Info.Width * codec.Info.Height;
                    long est = srcPx * srScale * srScale * 4 + srcPx * 8 + 96L * 1024 * 1024;
                    if (est > maxEstimate) maxEstimate = est;
                }
                catch
                {
                }
            }
            if (maxEstimate > 0)
                parallelDegree = (int)Math.Clamp(memoryBudget / maxEstimate, 1, Math.Min(4, Environment.ProcessorCount));
        }

        foreach (var item in FileItems)
            item.Status = FileStatus.Pending;

        await Parallel.ForEachAsync(FileItems,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelDegree,
                CancellationToken = _cts.Token
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

        IsBusy = false;

        if (FailedCount == 0)
            _notificationService.ShowSuccess(LocalizationRegistry.Get("ImageConvert.Msg_AllDone", TotalCount));
        else
            _notificationService.ShowWarn(LocalizationRegistry.Get("ImageConvert.Msg_PartialFail", FailedCount, TotalCount));
    }

    private bool CanStartConvert() => !IsBusy && FileItems.Count > 0;

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
}
