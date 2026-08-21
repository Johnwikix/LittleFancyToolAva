using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FancyToolAva.Models;

public enum FileStatus
{
    Pending,
    Converting,
    Completed,
    Failed
}

public partial class ImageFileItem : ObservableObject
{
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);

    private FileStatus _status = FileStatus.Pending;
    private string? _errorMessage;
    private double _progress;
    private string _statusDisplay = "Pending";
    private string _progressDisplay = "";

    public FileStatus Status
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

    public ImageFileItem(string filePath)
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

    internal IFileItemOwner? Owner { get; set; }

    public string StatusDisplay => _statusDisplay;

    public string ProgressDisplay => _progressDisplay;

    private void RefreshStatusDisplay()
    {
        _statusDisplay = _status switch
        {
            FileStatus.Pending => FancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Pending"),
            FileStatus.Converting => FancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Converting"),
            FileStatus.Completed => FancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Done"),
            FileStatus.Failed => string.IsNullOrEmpty(_errorMessage)
                ? FancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Failed")
                : FancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_FailedWithError", _errorMessage),
            _ => ""
        };
        OnPropertyChanged(nameof(StatusDisplay));
    }

    private void RefreshProgressDisplay()
    {
        _progressDisplay = _status switch
        {
            FileStatus.Converting => $" {_progress * 100:F0}%",
            FileStatus.Completed => " 100%",
            _ => ""
        };
        OnPropertyChanged(nameof(ProgressDisplay));
    }
}

internal interface IFileItemOwner
{
    void Remove(ImageFileItem item);
}
