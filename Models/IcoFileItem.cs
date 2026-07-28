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
    private string _statusDisplay = "Pending";
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
            IcoFileStatus.Pending => LittleFancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Pending"),
            IcoFileStatus.Converting => LittleFancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Converting"),
            IcoFileStatus.Completed => LittleFancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Done"),
            IcoFileStatus.Failed => string.IsNullOrEmpty(_errorMessage)
                ? LittleFancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Failed")
                : LittleFancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_FailedWithError", _errorMessage),
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