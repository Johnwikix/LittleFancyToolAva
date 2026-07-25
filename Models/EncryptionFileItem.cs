using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LittleFancyToolAva.Models;

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
    private string _statusDisplay = "等待中";
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
            EncryptionFileStatus.Pending => "等待中",
            EncryptionFileStatus.Encrypting => "加密中",
            EncryptionFileStatus.Decrypting => "解密中",
            EncryptionFileStatus.Completed => "已完成",
            EncryptionFileStatus.Failed => string.IsNullOrEmpty(_errorMessage) ? "失败" : $"失败: {_errorMessage}",
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