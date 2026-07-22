using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
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
    private const long MaxImageSize = 256 * 1024 * 1024;
    private readonly ILogger<ImageConversionService> _logger;

    public ImageConversionService(ILogger<ImageConversionService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> ImageToBase64Async(string imagePath)
    {
        var fi = new FileInfo(imagePath);
        if (fi.Length > MaxImageSize)
            throw new InvalidOperationException($"Image too large ({fi.Length / 1024 / 1024}MB). Max: {MaxImageSize / 1024 / 1024}MB");

        byte[] bytes = await File.ReadAllBytesAsync(imagePath);
        string base64 = Convert.ToBase64String(bytes);
        _logger.LogDebug("Image converted to Base64: {Path} ({Size} bytes -> {B64Length} chars)", imagePath, bytes.Length, base64.Length);
        return base64;
    }

    public async Task<Bitmap?> Base64ToBitmapAsync(string base64)
    {
        if (base64.Length > MaxImageSize * 4 / 3 + 100)
            throw new InvalidOperationException("Base64 input too large");

        byte[] bytes = await Task.Run(() => Convert.FromBase64String(base64));
        using MemoryStream ms = new(bytes);
        return await Task.Run(() => new Bitmap(ms));
    }

    public async Task<string?> ConvertImageFormatAsync(string inputPath, string outputPath, string format)
    {
        string tmpPath = outputPath + ".tmp";
        try
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
            await image.SaveAsync(tmpPath, encoder);
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            File.Move(tmpPath, outputPath);
            _logger.LogInformation("Image converted: {Input} -> {Output} ({Format})", inputPath, outputPath, format);
            return outputPath;
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            _logger.LogWarning("Image conversion failed: {Input}", inputPath);
            throw;
        }
    }

    public async Task<Bitmap?> LoadImageAsync(string imagePath)
    {
        var fi = new FileInfo(imagePath);
        if (fi.Length > MaxImageSize)
            throw new InvalidOperationException($"Image too large ({fi.Length / 1024 / 1024}MB). Max: {MaxImageSize / 1024 / 1024}MB");

        await using FileStream fs = File.OpenRead(imagePath);
        return await Task.Run(() => new Bitmap(fs));
    }

    public async Task<byte[]> ImageToBytesAsync(string imagePath)
    {
        var fi = new FileInfo(imagePath);
        if (fi.Length > MaxImageSize)
            throw new InvalidOperationException($"Image too large ({fi.Length / 1024 / 1024}MB). Max: {MaxImageSize / 1024 / 1024}MB");

        return await File.ReadAllBytesAsync(imagePath);
    }
}
