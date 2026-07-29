using Avalonia.Media.Imaging;

namespace LittleFancyToolAva.Services;

public interface IImageConversionService
{
    Task<string?> ImageToBase64Async(string imagePath, CancellationToken ct = default);
    Task<Bitmap?> Base64ToBitmapAsync(string base64, CancellationToken ct = default);
    Task<string?> ConvertImageFormatAsync(string inputPath, string outputPath, string format, CancellationToken ct = default, int? maxDimension = null, string? filterType = null, IProgress<double>? progress = null, int? scalePercent = null);
    Task<Bitmap?> LoadImageAsync(string imagePath, CancellationToken ct = default);
    Task<byte[]> ImageToBytesAsync(string imagePath, CancellationToken ct = default);
    Task<byte[]> CreateIcoBytesAsync(string imagePath, int size, CancellationToken ct = default);
    Task<bool> SaveAsIcoAsync(string imagePath, string outputPath, int size, CancellationToken ct = default);
}
