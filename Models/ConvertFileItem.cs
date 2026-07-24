using CommunityToolkit.Mvvm.ComponentModel;

namespace LittleFancyToolAva.Models;

public enum ConvertFileStatus
{
    Pending,
    Converting,
    Completed,
    Failed
}

public partial class ConvertFileItem : ObservableObject
{
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);

    public ConvertFileStatus Status
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(StatusDisplay));
        }
    } = ConvertFileStatus.Pending;

    public string? ErrorMessage
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(StatusDisplay));
        }
    }

    public ConvertFileItem(string filePath)
    {
        FilePath = filePath;
    }

    public string StatusDisplay => Status switch
    {
        ConvertFileStatus.Pending => "等待中",
        ConvertFileStatus.Converting => "转换中",
        ConvertFileStatus.Completed => "已完成",
        ConvertFileStatus.Failed => $"失败: {ErrorMessage}",
        _ => ""
    };
}
