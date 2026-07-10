using CommunityToolkit.Mvvm.ComponentModel;

namespace LittleFancyToolAva.Models;

public enum CompareState
{
    Match,
    Different,
    SourceOnly,
    TargetOnly
}

public partial class FolderCompareResult : ObservableObject
{
    [ObservableProperty]
    private string _relativePath = string.Empty;

    [ObservableProperty]
    private CompareState _state;

    [ObservableProperty]
    private string? _sourceDetail;

    [ObservableProperty]
    private string? _targetDetail;
}
