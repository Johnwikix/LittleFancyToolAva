using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LittleFancyToolAva.Models;

public enum IcoFileStatus
{
    Pending,
    Converting,
    Completed,
    Failed
}

public partial class IcoFileItem : ObservableObject
{
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);

    private IcoFileStatus _status = IcoFileStatus.Pending;
    private string? _errorMessage;
    private double _progress;
    private string _statusDisplay = "等待中";
    private string _progressDisplay = "";

    public IcoFileStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
                RefreshStatusDisplay();
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
                RefreshStatusDisplay();
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (SetProperty(ref _progress, value))
                RefreshProgressDisplay();
        }
    }

    public IcoFileItem(string filePath)
    {
        FilePath = filePath;
    }

    [RelayCommand]
    private void Remove()
    {
        if (Owner is { } owner)
        {
            owner.Remove(this);
        }
    }

    internal IIcoFileItemOwner? Owner { get; set; }

    public string StatusDisplay => _statusDisplay;

    public string ProgressDisplay => _progressDisplay;

    private void RefreshStatusDisplay()
    {
        _statusDisplay = _status switch
        {
            IcoFileStatus.Pending => "等待中",
            IcoFileStatus.Converting => "转换中",
            IcoFileStatus.Completed => "已完成",
            IcoFileStatus.Failed => string.IsNullOrEmpty(_errorMessage) ? "失败" : $"失败: {_errorMessage}",
            _ => ""
        };
        OnPropertyChanged(nameof(StatusDisplay));
    }

    private void RefreshProgressDisplay()
    {
        _progressDisplay = _status switch
        {
            IcoFileStatus.Converting => $" {_progress * 100:F0}%",
            IcoFileStatus.Completed => " 100%",
            _ => ""
        };
        OnPropertyChanged(nameof(ProgressDisplay));
    }
}

internal interface IIcoFileItemOwner
{
    void Remove(IcoFileItem item);
}