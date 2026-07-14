using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels;

public partial class FolderCompareViewModel : ViewModelBase, IViewState
{
    private readonly IFolderCompareService _folderCompareService;
    private readonly IFileDialogService _fileDialogService;
    private readonly INotificationService _notificationService;
    private readonly IViewStateService _viewStateService;

    string IViewState.ViewName => "folderCompareView";

    [ObservableProperty]
    private string _sourceFolder = string.Empty;

    [ObservableProperty]
    private string _targetFolder = string.Empty;

    [ObservableProperty]
    private bool _useHashComparison = true;

    [ObservableProperty]
    private bool _useMusicTitleComparison;

    [ObservableProperty]
    private bool _isComparing;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private ObservableCollection<FolderCompareResult> _compareResults = [];

    [ObservableProperty]
    private string _statusText = string.Empty;

    public FolderCompareViewModel(
        IFolderCompareService folderCompareService,
        IFileDialogService fileDialogService,
        INotificationService notificationService,
        IViewStateService viewStateService)
    {
        _folderCompareService = folderCompareService;
        _fileDialogService = fileDialogService;
        _notificationService = notificationService;
        _viewStateService = viewStateService;
        _viewStateService.Register(this);
    }

    object IViewState.CaptureState() => new FolderCompareViewState
    {
        SourceFolder = SourceFolder,
        TargetFolder = TargetFolder,
        UseHashComparison = UseHashComparison,
        UseMusicTitleComparison = UseMusicTitleComparison
    };

    void IViewState.RestoreState(object state)
    {
        if (state is FolderCompareViewState s)
        {
            SourceFolder = s.SourceFolder;
            TargetFolder = s.TargetFolder;
            UseHashComparison = s.UseHashComparison;
            UseMusicTitleComparison = s.UseMusicTitleComparison;
        }
    }

    [RelayCommand]
    private async Task SelectSourceFolder()
    {
        string? folder = await _fileDialogService.PickFolderAsync("选择源文件夹");
        if (folder != null)
            SourceFolder = folder;
    }

    [RelayCommand]
    private async Task SelectTargetFolder()
    {
        string? folder = await _fileDialogService.PickFolderAsync("选择目标文件夹");
        if (folder != null)
            TargetFolder = folder;
    }

    [RelayCommand]
    private async Task Compare()
    {
        if (string.IsNullOrEmpty(SourceFolder) || string.IsNullOrEmpty(TargetFolder))
        {
            _notificationService.ShowWarn("请先选择源文件夹和目标文件夹");
            return;
        }
        if (!Directory.Exists(SourceFolder))
        {
            _notificationService.ShowError("源文件夹不存在");
            return;
        }
        if (!Directory.Exists(TargetFolder))
        {
            _notificationService.ShowError("目标文件夹不存在");
            return;
        }

        IsComparing = true;
        ProgressValue = 0;
        StatusText = "正在比较...";
        CompareResults.Clear();

        var progress = new Progress<double>(p =>
        {
            ProgressValue = p;
        });

        try
        {
            var results = await _folderCompareService.CompareFoldersAsync(
                SourceFolder, TargetFolder,
                UseHashComparison, UseMusicTitleComparison, progress);

            CompareResults = [.. results];

            int matchCount = results.Count(r => r.State == CompareState.Match);
            int diffCount = results.Count(r => r.State == CompareState.Different);
            int sourceOnly = results.Count(r => r.State == CompareState.SourceOnly);
            int targetOnly = results.Count(r => r.State == CompareState.TargetOnly);
            StatusText = $"匹配: {matchCount}, 不同: {diffCount}, 仅源文件夹: {sourceOnly}, 仅目标文件夹: {targetOnly}";

            _notificationService.ShowInfo($"比较完成，共 {results.Count} 个文件");
        }
        catch (Exception ex)
        {
            StatusText = $"比较失败: {ex.Message}";
            _notificationService.ShowError($"比较失败: {ex.Message}");
        }
        finally
        {
            IsComparing = false;
            ProgressValue = 1;
        }
    }
}
