namespace LittleFancyToolAva.Services;

public interface IFileEncryptionService
{
    Task EncryptFileAsync(string inputFilePath, string outputFilePath, string key, string iv, IProgress<double>? progress = null, CancellationToken cancellationToken = default, string? keyIvType = "text");
    Task DecryptFileAsync(string inputFilePath, string outputFilePath, string key, string iv, IProgress<double>? progress = null, CancellationToken cancellationToken = default, string? keyIvType = "text");
}