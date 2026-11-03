namespace FancyToolAva.Services;

public interface IImageConversionService
{
    Task<string?> ImageToBase64Async(string imagePath, CancellationToken ct = default);
    Task<byte[]?> Base64ToPreviewPngAsync(string base64, CancellationToken ct = default);
    Task<string?> ConvertImageFormatAsync(
        string inputPath,
        string outputPath,
        string format,
        CancellationToken ct = default,
        int? maxDimension = null,
        string? filterType = null,
        IProgress<double>? progress = null,
        int? scalePercent = null,
        SuperResolutionModel? superResolutionModel = null,
        int superResolutionScale = 4,
        ISuperResolutionService? superResolutionService = null);
    Task<byte[]?> LoadPreviewPngAsync(string imagePath, CancellationToken ct = default);
    Task<byte[]> ImageToBytesAsync(string imagePath, CancellationToken ct = default);
    Task<byte[]> CreateIcoBytesAsync(string imagePath, int size, CancellationToken ct = default);
    Task<bool> SaveAsIcoAsync(string imagePath, string outputPath, int size, CancellationToken ct = default);
}
