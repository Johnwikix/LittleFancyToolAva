using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.Utils;

namespace LittleFancyToolAva.ViewModels;

public partial class Img2icoViewModel : ViewModelBase, IViewState, IIcoFileItemOwner
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tiff", ".gif", ".dds", ".jxl", ".heic"
    };

    private readonly IIconConversionService _iconConversionService;
    private readonly IFileDialogService _fileDialogService;
    private readonly INotificationService _notificationService;
    private readonly IViewStateService _viewStateService;
    private CancellationTokenSource? _cts;
    private int _completedCountField;
    private int _failedCountField;

    string IViewState.ViewName => "img2icoView";

    public ObservableCollection<IcoFileItem> FileItems { get; } = [];

    public IcoFileItem? SelectedFileItem
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

    public int SelectedSizeIndex
    {
        get => field;
        set => SetProperty(ref field, value);
    } = 2;

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

    public List<int> AvailableSizes { get; } = [16, 32, 48, 64, 128, 256];

    public Img2icoViewModel(
        IIconConversionService iconConversionService,
        IFileDialogService fileDialogService,
        INotificationService notificationService,
        IViewStateService viewStateService)
    {
        _iconConversionService = iconConversionService;
        _fileDialogService = fileDialogService;
        _notificationService = notificationService;
        _viewStateService = viewStateService;
        _viewStateService.Register(this);
        FileItems.CollectionChanged += (_, _) => StartConvertCommand.NotifyCanExecuteChanged();
        UpdateStatusText();
    }

    object IViewState.CaptureState() => new Img2icoViewState
    {
        SelectedSizeIndex = SelectedSizeIndex,
        OutputFolder = OutputFolder
    };

    void IViewState.RestoreState(object state)
    {
        if (state is Img2icoViewState s)
        {
            SelectedSizeIndex = s.SelectedSizeIndex;
            OutputFolder = s.OutputFolder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AddFiles()
    {
        IReadOnlyList<FilePickerFileType> filters = [new(LocalizationRegistry.Get("Img2Ico.Picker_ImageFile")) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tiff", "*.gif", "*.dds", "*.jxl", "*.heic"] }];
        var paths = await _fileDialogService.PickOpenFilesAsync(LocalizationRegistry.Get("Img2Ico.Picker_SelectImage"), filters);
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
        string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("Img2Ico.Picker_SelectImageFolder"));
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

    void IIcoFileItemOwner.Remove(IcoFileItem item)
    {
        FileItems.Remove(item);
        UpdateStatusText();
    }

    private IcoFileItem CreateItem(string path)
    {
        var item = new IcoFileItem(path);
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
        string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("Img2Ico.Picker_SelectOutputDir"));
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
            string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("Img2Ico.Picker_SelectOutputDir"));
            if (folder == null) return;
            OutputFolder = folder;
        }

        if (!Directory.Exists(OutputFolder))
        {
            _notificationService.ShowError(LocalizationRegistry.Get("Img2Ico.Msg_OutputDirMissing"));
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

        int size = AvailableSizes[SelectedSizeIndex];

        foreach (var item in FileItems)
            item.Status = IcoFileStatus.Pending;

        await Parallel.ForEachAsync(FileItems,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = _cts.Token
            },
            async (item, ct) =>
            {
                item.Status = IcoFileStatus.Converting;
                item.Progress = 0;
                var progress = new ThrottledProgress<double>(p =>
                {
                    item.Progress = p;
                }, TimeSpan.FromMilliseconds(50));
                try
                {
                    string outputPath = GetUniqueOutputPath(OutputFolder!, item.FileName);
                    await _iconConversionService.SaveAsIcoAsync(item.FilePath, outputPath, size, ct);
                    item.Progress = 1.0;
                    item.Status = IcoFileStatus.Completed;
                }
                catch (OperationCanceledException)
                {
                    item.Status = IcoFileStatus.Pending;
                }
                catch (Exception ex)
                {
                    item.Status = IcoFileStatus.Failed;
                    item.ErrorMessage = ex.Message;
                }
                finally
                {
                    bool isFailed = item.Status == IcoFileStatus.Failed;
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
            _notificationService.ShowSuccess(LocalizationRegistry.Get("Img2Ico.Msg_AllDone", TotalCount));
        else
            _notificationService.ShowWarn(LocalizationRegistry.Get("Img2Ico.Msg_PartialFail", FailedCount, TotalCount));
    }

    private bool CanStartConvert() => !IsBusy && FileItems.Count > 0;

    private static string GetUniqueOutputPath(string folder, string fileName)
    {
        string basePath = Path.Combine(folder, Path.ChangeExtension(fileName, ".ico"));
        if (!File.Exists(basePath))
            return basePath;

        string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(folder, $"{nameWithoutExt}_{i}.ico");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private void UpdateStatusText()
    {
        StatusText = FileItems.Count == 0
            ? LocalizationRegistry.Get("Img2Ico.Status_Files")
            : LocalizationRegistry.Get("Img2Ico.Status_FileCount", FileItems.Count);
    }
}