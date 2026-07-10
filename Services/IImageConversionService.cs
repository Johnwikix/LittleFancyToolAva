using Avalonia.Media.Imaging;

namespace LittleFancyToolAva.Services;

public interface IImageConversionService
{
    Task<string?> ImageToBase64Async(string imagePath);
    Task<Bitmap?> Base64ToBitmapAsync(string base64);
    Task<string?> ConvertImageFormatAsync(string inputPath, string outputPath, string format);
    Task<Bitmap?> LoadImageAsync(string imagePath);
    Task<byte[]> ImageToBytesAsync(string imagePath);
}
