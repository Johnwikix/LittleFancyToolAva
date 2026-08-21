using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FancyToolAva.Models;

public enum EncryptionFileStatus
{
    Pending,
    Encrypting,
    Decrypting,
    Completed,
    Failed
}

public partial class EncryptionFileItem : ObservableObject
{
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);

    private string? _outputPath;
    private EncryptionFileStatus _status = EncryptionFileStatus.Pending;
    private string? _errorMessage;
    private double _progress;
    private string _statusDisplay = "Pending";
    private string _progressDisplay = "";

    public string? OutputPath
    {
        get => _outputPath;
        set => SetProperty(ref _outputPath, value);
    }

    public EncryptionFileStatus Status
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

    public EncryptionFileItem(string filePath)
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

    internal IEncryptionFileItemOwner? Owner { get; set; }

    public string StatusDisplay => _statusDisplay;

    public string ProgressDisplay => _progressDisplay;

    private void RefreshStatusDisplay()
    {
        _statusDisplay = _status switch
        {
            EncryptionFileStatus.Pending => FancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Pending"),
            EncryptionFileStatus.Encrypting => FancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Encrypting"),
            EncryptionFileStatus.Decrypting => FancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Decrypting"),
            EncryptionFileStatus.Completed => FancyToolAva.Services.LocalizationRegistry.Get("FileItem.Status_Done"),
            EncryptionFileStatus.Failed => string.IsNullOrEmpty(_errorMessage)
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
            EncryptionFileStatus.Encrypting => $" {_progress * 100:F0}%",
            EncryptionFileStatus.Decrypting => $" {_progress * 100:F0}%",
            EncryptionFileStatus.Completed => " 100%",
            _ => ""
        };
        OnPropertyChanged(nameof(ProgressDisplay));
    }
}

internal interface IEncryptionFileItemOwner
{
    void Remove(EncryptionFileItem item);
}