using CommunityToolkit.Mvvm.ComponentModel;

namespace FancyToolAva.Models;

public enum CompareState
{
    Match,
    Different,
    SourceOnly,
    TargetOnly
}

public partial class FolderCompareResult : ObservableObject
{
    public string RelativePath
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public CompareState State
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string? SourceDetail
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string? TargetDetail
    {
        get;
        set => SetProperty(ref field, value);
    }
}
