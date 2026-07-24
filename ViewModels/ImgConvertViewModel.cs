using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels;

public partial class ImgConvertViewModel : ViewModelBase, IViewState
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tiff", ".gif", ".dds", ".jxl", ".heic"
    };

    private readonly IImageConversionService _imageConversionService;
    private readonly IFileDialogService _fileDialogService;
    private readonly INotificationService _notificationService;
    private readonly IViewStateService _viewStateService;
    private CancellationTokenSource? _cts;
    private int _completedCountField;
    private int _failedCountField;

    string IViewState.ViewName => "imgConvertView";

    public ObservableCollection<ConvertFileItem> FileItems { get; } = [];

    public ConvertFileItem? SelectedFileItem
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
        set => SetProperty(ref field, value);
    }

    public bool IsDownscaleEnabled
    {
        get => field;
        set => SetProperty(ref field, value);
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

    public List<string> AvailableFormats { get; } = ["jpg", "png", "bmp", "webp", "tiff", "dds", "jxl", "heic"];
    public List<string> AvailableFilters { get; } = ["Lanczos", "Mitchell", "Catrom", "Cubic", "Triangle", "Box"];

    public ImgConvertViewModel(
        IImageConversionService imageConversionService,
        IFileDialogService fileDialogService,
        INotificationService notificationService,
        IViewStateService viewStateService)
    {
        _imageConversionService = imageConversionService;
        _fileDialogService = fileDialogService;
        _notificationService = notificationService;
        _viewStateService = viewStateService;
        _viewStateService.Register(this);
        FileItems.CollectionChanged += (_, _) => StartConvertCommand.NotifyCanExecuteChanged();
        UpdateStatusText();
    }

    private int? GetMaxDimension()
    {
        if (!IsDownscaleEnabled || DownscalePercent >= 100) return null;
        return DownscalePercent;
    }

    object IViewState.CaptureState() => new ImgConvertViewState
    {
        FormatIndex = FormatIndex,
        OutputFolder = OutputFolder,
        IsDownscaleEnabled = IsDownscaleEnabled,
        DownscalePercent = DownscalePercent,
        SelectedFilterIndex = SelectedFilterIndex
    };

    void IViewState.RestoreState(object state)
    {
        if (state is ImgConvertViewState s)
        {
            FormatIndex = s.FormatIndex;
            OutputFolder = s.OutputFolder;
            IsDownscaleEnabled = s.IsDownscaleEnabled;
            DownscalePercent = s.DownscalePercent is > 10 and <= 100 ? s.DownscalePercent : 100;
            SelectedFilterIndex = s.SelectedFilterIndex;
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AddFiles()
    {
        IReadOnlyList<FilePickerFileType> filters = [new("图片文件") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tiff", "*.gif", "*.dds", "*.jxl", "*.heic"] }];
        var paths = await _fileDialogService.PickOpenFilesAsync("选择图片", filters);
        if (paths == null) return;

        var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (!existing.Contains(path))
            {
                FileItems.Add(new ConvertFileItem(path));
                existing.Add(path);
            }
        }
        UpdateStatusText();
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AddFolder()
    {
        string? folder = await _fileDialogService.PickFolderAsync("选择图片文件夹");
        if (folder == null) return;

        var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (ImageExtensions.Contains(Path.GetExtension(file)) && !existing.Contains(file))
            {
                FileItems.Add(new ConvertFileItem(file));
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

    private bool CanModify() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task PickOutputFolder()
    {
        string? folder = await _fileDialogService.PickFolderAsync("选择输出目录");
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
            string? folder = await _fileDialogService.PickFolderAsync("选择输出目录");
            if (folder == null) return;
            OutputFolder = folder;
        }

        if (!Directory.Exists(OutputFolder))
        {
            _notificationService.ShowError("输出目录不存在");
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
        string? filter = IsDownscaleEnabled ? AvailableFilters[SelectedFilterIndex] : null;
        int? scalePct = IsDownscaleEnabled && DownscalePercent < 100 ? DownscalePercent : null;

        foreach (var item in FileItems)
            item.Status = ConvertFileStatus.Pending;

        await Parallel.ForEachAsync(FileItems,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = _cts.Token
            },
            async (item, ct) =>
            {
                item.Status = ConvertFileStatus.Converting;
                try
                {
                    string outputPath = GetUniqueOutputPath(OutputFolder!, item.FileName, format);
                    await _imageConversionService.ConvertImageFormatAsync(item.FilePath, outputPath, format, ct, null, filter, scalePercent: scalePct);
                    item.Status = ConvertFileStatus.Completed;
                }
                catch (OperationCanceledException)
                {
                    item.Status = ConvertFileStatus.Pending;
                }
                catch (Exception ex)
                {
                    item.Status = ConvertFileStatus.Failed;
                    item.ErrorMessage = ex.Message;
                }
                finally
                {
                    bool isFailed = item.Status == ConvertFileStatus.Failed;
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
            _notificationService.ShowSuccess($"全部转换完成（共 {TotalCount} 个）");
        else
            _notificationService.ShowWarn($"转换完成，{FailedCount} 个失败 / {TotalCount} 个");
    }

    private bool CanStartConvert() => !IsBusy && FileItems.Count > 0;

    private static string GetUniqueOutputPath(string folder, string fileName, string format)
    {
        string basePath = Path.Combine(folder, Path.ChangeExtension(fileName, format));
        if (!File.Exists(basePath))
            return basePath;

        string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        string ext = "." + format;
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
            ? "文件列表"
            : $"共 {FileItems.Count} 个文件";
    }
}
