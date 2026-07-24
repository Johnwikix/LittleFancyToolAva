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

public partial class FileEncryptionViewModel : ViewModelBase, IViewState
{
    private readonly IFileEncryptionService _fileEncryptionService;
    private readonly IFileDialogService _fileDialogService;
    private readonly INotificationService _notificationService;
    private readonly IViewStateService _viewStateService;
    private CancellationTokenSource? _cts;
    private int _completedCountField;
    private int _failedCountField;
    private static readonly object _uniquePathLock = new();

    string IViewState.ViewName => "fileEncryptionView";

    public ObservableCollection<EncryptionFileItem> FileItems { get; } = [];

    public EncryptionFileItem? SelectedFileItem
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    public string? OutputDirectory
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    public string Key
    {
        get => field;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string Iv
    {
        get => field;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public int KeyLengthIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnKeyLengthIndexChanged(value);
            }
        }
    }

    public int KeyIvTypeIndex
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                GenerateKey();
            }
        }
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
                SelectOutputDirectoryCommand.NotifyCanExecuteChanged();
                StartEncryptCommand.NotifyCanExecuteChanged();
                StartDecryptCommand.NotifyCanExecuteChanged();
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

    public double ProgressValue
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    public string StatusText
    {
        get => field;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public FileEncryptionViewModel(
        IFileEncryptionService fileEncryptionService,
        IFileDialogService fileDialogService,
        INotificationService notificationService,
        IViewStateService viewStateService)
    {
        _fileEncryptionService = fileEncryptionService;
        _fileDialogService = fileDialogService;
        _notificationService = notificationService;
        _viewStateService = viewStateService;
        GenerateKey();
        _viewStateService.Register(this);
        FileItems.CollectionChanged += (_, _) =>
        {
            StartEncryptCommand.NotifyCanExecuteChanged();
            StartDecryptCommand.NotifyCanExecuteChanged();
            UpdateStatusText();
        };
    }

    object IViewState.CaptureState() => new FileEncryptionViewState
    {
        KeyLengthIndex = KeyLengthIndex,
        OutputDirectory = OutputDirectory
    };

    void IViewState.RestoreState(object state)
    {
        if (state is FileEncryptionViewState s)
        {
            KeyLengthIndex = s.KeyLengthIndex;
            OutputDirectory = s.OutputDirectory;
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AddFiles()
    {
        IReadOnlyList<FilePickerFileType> filters = [new("All Files") { Patterns = ["*.*"] }];
        var paths = await _fileDialogService.PickOpenFilesAsync("选择文件", filters);
        if (paths == null) return;

        var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (!existing.Contains(path))
            {
                FileItems.Add(new EncryptionFileItem(path));
                existing.Add(path);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AddFolder()
    {
        string? folder = await _fileDialogService.PickFolderAsync("选择文件夹");
        if (folder == null) return;

        var existing = new HashSet<string>(FileItems.Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (!existing.Contains(file))
            {
                FileItems.Add(new EncryptionFileItem(file));
                existing.Add(file);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private void ClearList()
    {
        FileItems.Clear();
    }

    private bool CanModify() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task SelectOutputDirectory()
    {
        string? folder = await _fileDialogService.PickFolderAsync("选择输出目录");
        if (folder != null)
        {
            OutputDirectory = folder;
        }
    }

    [RelayCommand]
    private void GenerateKey()
    {
        int[] keyLengths = [128, 192, 256];
        int bitLen = keyLengths[KeyLengthIndex >= 0 && KeyLengthIndex < keyLengths.Length ? KeyLengthIndex : 0];
        string keyIvType = GetSelectedKeyIvType();
        Key = ToolMethod.GenerateSymmetricKey(bitLen, keyIvType);
        Iv = ToolMethod.GenerateSymmetricKey(128, keyIvType);
    }

    private string GetSelectedKeyIvType() => KeyIvTypeIndex switch
    {
        0 => "text",
        1 => "base64",
        2 => "hex",
        _ => "text"
    };

    private void OnKeyLengthIndexChanged(int value)
    {
        GenerateKey();
    }

    [RelayCommand(CanExecute = nameof(CanStartProcess))]
    private async Task StartEncrypt()
    {
        await ProcessAsync(true);
    }

    [RelayCommand(CanExecute = nameof(CanStartProcess))]
    private async Task StartDecrypt()
    {
        await ProcessAsync(false);
    }

    private bool CanStartProcess() => !IsBusy && FileItems.Count > 0;

    private async Task ProcessAsync(bool encrypt)
    {
        if (string.IsNullOrEmpty(OutputDirectory))
        {
            string? folder = await _fileDialogService.PickFolderAsync("选择输出目录");
            if (folder == null) return;
            OutputDirectory = folder;
        }

        if (!Directory.Exists(OutputDirectory))
        {
            _notificationService.ShowError("输出目录不存在");
            return;
        }

        if (string.IsNullOrEmpty(Key) || string.IsNullOrEmpty(Iv))
        {
            _notificationService.ShowWarn("请生成 Key 和 IV");
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
        ProgressValue = 0;
        UpdateStatusText();

        string action = encrypt ? "加密" : "解密";
        var inProgress = encrypt ? EncryptionFileStatus.Encrypting : EncryptionFileStatus.Decrypting;
        string keyIvType = GetSelectedKeyIvType();

        foreach (var item in FileItems)
        {
            item.Status = EncryptionFileStatus.Pending;
            string baseName = encrypt
                ? item.FileName + ".enc"
                : StripEncExtension(item.FileName);
            item.OutputPath = ResolveUniqueOutputPath(OutputDirectory!, baseName);
        }

        await Parallel.ForEachAsync(FileItems,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = _cts.Token
            },
            async (item, ct) =>
            {
                item.Status = inProgress;
                try
                {
                    string outputPath = item.OutputPath!;
                    if (encrypt)
                    {
                        await _fileEncryptionService.EncryptFileAsync(item.FilePath, outputPath, Key, Iv, null, ct, keyIvType);
                    }
                    else
                    {
                        await _fileEncryptionService.DecryptFileAsync(item.FilePath, outputPath, Key, Iv, null, ct, keyIvType);
                    }
                    item.Status = EncryptionFileStatus.Completed;
                }
                catch (OperationCanceledException)
                {
                    item.Status = EncryptionFileStatus.Pending;
                }
                catch (Exception ex)
                {
                    item.Status = EncryptionFileStatus.Failed;
                    item.ErrorMessage = ex.Message;
                }
                finally
                {
                    bool isFailed = item.Status == EncryptionFileStatus.Failed;
                    if (isFailed)
                        Interlocked.Increment(ref _failedCountField);
                    int processed = Interlocked.Increment(ref _completedCountField);
                    int failed = Volatile.Read(ref _failedCountField);
                    Dispatcher.UIThread.Post(() =>
                    {
                        CompletedCount = processed - failed;
                        FailedCount = failed;
                        ProgressValue = (double)processed / TotalCount;
                        UpdateStatusText();
                    });
                }
            });

        IsBusy = false;

        if (FailedCount == 0)
            _notificationService.ShowSuccess($"{action}完成(共 {TotalCount} 个)");
        else
            _notificationService.ShowWarn($"{action}完成，{FailedCount} 个失败 / {TotalCount} 个");
    }

    private string ResolveUniqueOutputPath(string folder, string baseName)
    {
        lock (_uniquePathLock)
        {
            string basePath = Path.Combine(folder, baseName);
            if (!File.Exists(basePath))
                return basePath;

            string nameWithoutExt = Path.GetFileNameWithoutExtension(baseName);
            string ext = Path.GetExtension(baseName);
            for (int i = 1; ; i++)
            {
                string candidate = Path.Combine(folder, $"{nameWithoutExt}_{i}{ext}");
                if (!File.Exists(candidate))
                    return candidate;
            }
        }
    }

    private static string StripEncExtension(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        return ext.Equals(".enc", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;
    }

    private void UpdateStatusText()
    {
        StatusText = FileItems.Count == 0
            ? "文件列表"
            : $"共 {FileItems.Count} 个文件";
    }
}