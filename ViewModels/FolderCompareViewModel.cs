using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.Services;

namespace FancyToolAva.ViewModels;

public partial class FolderCompareViewModel : ViewModelBase, IViewState
{
    private readonly IFolderCompareService _folderCompareService;
    private readonly IFileDialogService _fileDialogService;
    private readonly INotificationService _notificationService;
    private readonly IViewStateService _viewStateService;
    private CancellationTokenSource? _compareCts;

    string IViewState.ViewName => "folderCompareView";

    public string SourceFolder
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string TargetFolder
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public bool UseHashComparison
    {
        get;
        set => SetProperty(ref field, value);
    } = true;

    public bool UseMusicTitleComparison
    {
        get;
        set => SetProperty(ref field, value);
    }

    public bool IsComparing
    {
        get;
        set => SetProperty(ref field, value);
    }

    public double ProgressValue
    {
        get;
        set => SetProperty(ref field, value);
    }

    public ObservableCollection<FolderCompareResult> CompareResults
    {
        get;
        set => SetProperty(ref field, value);
    } = [];

    public string StatusText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

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
        string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("FolderCompare.Picker_SelectSource"));
        if (folder != null)
            SourceFolder = folder;
    }

    [RelayCommand]
    private async Task SelectTargetFolder()
    {
        string? folder = await _fileDialogService.PickFolderAsync(LocalizationRegistry.Get("FolderCompare.Picker_SelectTarget"));
        if (folder != null)
            TargetFolder = folder;
    }

    [RelayCommand]
    private async Task Compare()
    {
        if (string.IsNullOrEmpty(SourceFolder) || string.IsNullOrEmpty(TargetFolder))
        {
            _notificationService.ShowWarn(LocalizationRegistry.Get("FolderCompare.Msg_NeedBothFolders"));
            return;
        }
        if (!Directory.Exists(SourceFolder))
        {
            _notificationService.ShowError(LocalizationRegistry.Get("FolderCompare.Msg_SourceMissing"));
            return;
        }
        if (!Directory.Exists(TargetFolder))
        {
            _notificationService.ShowError(LocalizationRegistry.Get("FolderCompare.Msg_TargetMissing"));
            return;
        }

        IsComparing = true;
        ProgressValue = 0;
        StatusText = LocalizationRegistry.Get("FolderCompare.Status_Comparing");
        CompareResults.Clear();

        _compareCts?.Cancel();
        _compareCts?.Dispose();
        _compareCts = new CancellationTokenSource();

        var progress = new Progress<double>(p =>
        {
            ProgressValue = p;
        });

        try
        {
            var results = await _folderCompareService.CompareFoldersAsync(
                SourceFolder, TargetFolder,
                UseHashComparison, UseMusicTitleComparison, progress, _compareCts.Token);

            CompareResults = [.. results];

            int matchCount = results.Count(r => r.State == CompareState.Match);
            int diffCount = results.Count(r => r.State == CompareState.Different);
            int sourceOnly = results.Count(r => r.State == CompareState.SourceOnly);
            int targetOnly = results.Count(r => r.State == CompareState.TargetOnly);
            StatusText = LocalizationRegistry.Get("FolderCompare.Status_ResultSummary", matchCount, diffCount, sourceOnly, targetOnly);

            _notificationService.ShowInfo(LocalizationRegistry.Get("FolderCompare.Msg_Done", results.Count));
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationRegistry.Get("FolderCompare.Status_Cancelled");
        }
        catch (Exception ex)
        {
            var failMsg = LocalizationRegistry.Get("FolderCompare.Msg_Fail", ex.Message);
            StatusText = failMsg;
            _notificationService.ShowError(failMsg);
        }
        finally
        {
            IsComparing = false;
            ProgressValue = 1;
        }
    }
}
