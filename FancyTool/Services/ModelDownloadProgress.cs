namespace FancyToolAva.Services;

public enum ModelDownloadStage
{
    Connecting,
    Downloading,
    Verifying,
    Done,
    Failed,
}

public sealed record ModelDownloadProgress(
    string FileName,
    ModelDownloadStage Stage,
    long BytesDownloaded,
    long? TotalBytes,
    string? Message = null);