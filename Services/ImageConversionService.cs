using Avalonia.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Processing;

namespace LittleFancyToolAva.Services;

public class ImageConversionService : IImageConversionService
{
    public async Task<string?> ImageToBase64Async(string imagePath)
    {
        byte[] bytes = await File.ReadAllBytesAsync(imagePath);
        return Convert.ToBase64String(bytes);
    }

    public async Task<Bitmap?> Base64ToBitmapAsync(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        using MemoryStream ms = new(bytes);
        return await Task.Run(() => new Bitmap(ms));
    }

    public async Task<string?> ConvertImageFormatAsync(string inputPath, string outputPath, string format)
    {
        using Image image = await SixLabors.ImageSharp.Image.LoadAsync(inputPath);
        IImageEncoder encoder = format.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => new JpegEncoder { Quality = 90 },
            "png" => new PngEncoder(),
            "gif" => new GifEncoder(),
            "bmp" => new BmpEncoder(),
            "webp" => new WebpEncoder(),
            "tiff" => new TiffEncoder(),
            _ => throw new NotSupportedException($"Format '{format}' is not supported.")
        };
        await image.SaveAsync(outputPath, encoder);
        return outputPath;
    }

    public async Task<Bitmap?> LoadImageAsync(string imagePath)
    {
        byte[] bytes = await File.ReadAllBytesAsync(imagePath);
        using MemoryStream ms = new(bytes);
        return await Task.Run(() => new Bitmap(ms));
    }

    public async Task<byte[]> ImageToBytesAsync(string imagePath)
    {
        return await File.ReadAllBytesAsync(imagePath);
    }
}
