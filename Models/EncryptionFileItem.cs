using CommunityToolkit.Mvvm.ComponentModel;

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

    public string? OutputPath
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    public EncryptionFileStatus Status
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(StatusDisplay));
        }
    } = EncryptionFileStatus.Pending;

    public string? ErrorMessage
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(StatusDisplay));
        }
    }

    public double Progress
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(ProgressDisplay));
        }
    }

    public EncryptionFileItem(string filePath)
    {
        FilePath = filePath;
    }

    public string StatusDisplay => Status switch
    {
        EncryptionFileStatus.Pending => "等待中",
        EncryptionFileStatus.Encrypting => "加密中",
        EncryptionFileStatus.Decrypting => "解密中",
        EncryptionFileStatus.Completed => "已完成",
        EncryptionFileStatus.Failed => $"失败: {ErrorMessage}",
        _ => ""
    };

    public string ProgressDisplay => Status switch
    {
        EncryptionFileStatus.Encrypting => $" {Progress * 100:F0}%",
        EncryptionFileStatus.Decrypting => $" {Progress * 100:F0}%",
        EncryptionFileStatus.Completed => " 100%",
        _ => ""
    };
}