using Avalonia.Platform.Storage;
using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    string IViewState.ViewName => "fileEncryptionView";

    [ObservableProperty]
    private ObservableCollection<string> _selectedFiles = [];

    [ObservableProperty]
    private string _selectedFilesDisplay = string.Empty;

    [ObservableProperty]
    private string _outputDirectory = string.Empty;

    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _iv = string.Empty;

    [ObservableProperty]
    private int _keyLengthIndex;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private string _outputLog = string.Empty;

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
    }

    object IViewState.CaptureState() => new FileEncryptionViewState
    {
        KeyLengthIndex = KeyLengthIndex
    };

    void IViewState.RestoreState(object state)
    {
        if (state is FileEncryptionViewState s)
        {
            KeyLengthIndex = s.KeyLengthIndex;
        }
    }

    [RelayCommand]
    private async Task SelectFiles()
    {
        IReadOnlyList<FilePickerFileType> filters = [new("All Files") { Patterns = ["*.*"] }];
        var files = await _fileDialogService.PickOpenFilesAsync("选择文件", filters);
        if (files is { Count: > 0 })
        {
            SelectedFiles = [.. files];
            SelectedFilesDisplay = string.Join("; ", files.Select(Path.GetFileName));
        }
    }

    [RelayCommand]
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
        Key = ToolMethod.GenerateSymmetricKey(bitLen, "hex");
        Iv = ToolMethod.GenerateSymmetricKey(128, "hex");
    }

    partial void OnKeyLengthIndexChanged(int value)
    {
        GenerateKey();
    }

    [RelayCommand]
    private async Task Encrypt()
    {
        await ProcessFiles(true);
    }

    [RelayCommand]
    private async Task Decrypt()
    {
        await ProcessFiles(false);
    }

    private async Task ProcessFiles(bool encrypt)
    {
        if (SelectedFiles.Count == 0)
        {
            _notificationService.ShowWarn("请先选择文件");
            return;
        }
        if (string.IsNullOrEmpty(OutputDirectory))
        {
            _notificationService.ShowWarn("请选择输出目录");
            return;
        }
        if (string.IsNullOrEmpty(Key) || string.IsNullOrEmpty(Iv))
        {
            _notificationService.ShowWarn("请生成 Key 和 IV");
            return;
        }

        IsProcessing = true;
        ProgressValue = 0;
        OutputLog = string.Empty;

        var progress = new Progress<double>(p => ProgressValue = p);
        string action = encrypt ? "加密" : "解密";

        foreach (string file in SelectedFiles)
        {
            try
            {
                string fileName = Path.GetFileName(file);
                string ext = encrypt ? ".enc" : ".dec";
                string outputFile = Path.Combine(OutputDirectory, fileName + ext);

                if (encrypt)
                {
                    await _fileEncryptionService.EncryptFileAsync(file, outputFile, Key, Iv, progress);
                }
                else
                {
                    await _fileEncryptionService.DecryptFileAsync(file, outputFile, Key, Iv, progress);
                }

                OutputLog += $"[OK] {fileName} {action}完成 -> {outputFile}\n";
            }
            catch (Exception ex)
            {
                OutputLog += $"[FAIL] {Path.GetFileName(file)} {action}失败: {ex.Message}\n";
            }
        }

        IsProcessing = false;
        ProgressValue = 1;
        _notificationService.ShowSuccess($"{action}完成");
    }
}
