using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace LittleFancyToolAva.Services;

public class ImageConversionService : IImageConversionService
{
    private const long MaxImageSize = 1024 * 1024 * 1024;
    private const int PreviewMaxDimension = 1920;
    private const int WebpMaxDimension = 16383;
    private readonly ILogger<ImageConversionService> _logger;

    public ImageConversionService(ILogger<ImageConversionService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> ImageToBase64Async(string imagePath, CancellationToken ct = default)
    {
        var fi = new FileInfo(imagePath);
        if (fi.Length > MaxImageSize)
            throw new InvalidOperationException($"Image too large ({fi.Length / 1024 / 1024}MB). Max: {MaxImageSize / 1024 / 1024}MB");

        byte[] bytes = await File.ReadAllBytesAsync(imagePath, ct);
        return await Task.Run(() =>
        {
            string base64 = Convert.ToBase64String(bytes);
            _logger.LogDebug("Image converted to Base64: {Path} ({Size} bytes -> {B64Length} chars)", imagePath, bytes.Length, base64.Length);
            return base64;
        }, ct);
    }

    public async Task<Bitmap?> Base64ToBitmapAsync(string base64, CancellationToken ct = default)
    {
        if (base64.Length > MaxImageSize * 4 / 3 + 100)
            throw new InvalidOperationException("Base64 input too large");

        byte[] bytes = await Task.Run(() => Convert.FromBase64String(base64), ct);
        return await Task.Run(() =>
        {
            using var src = SKBitmap.Decode(bytes);
            if (src == null) return null;

            byte[] pngBytes = EncodePreviewPng(src);

            using var ms = new MemoryStream(pngBytes);
            return new Bitmap(ms);
        }, ct);
    }

    public async Task<string?> ConvertImageFormatAsync(string inputPath, string outputPath, string format, CancellationToken ct = default, int? maxDimension = null, string? filterType = null, IProgress<double>? progress = null, int? scalePercent = null)
    {
        string tmpPath = outputPath + ".tmp";
        var (outputFormat, quality) = MapFormat(format);

        int? effectiveMaxDim = maxDimension;

        if (outputFormat == SKEncodedImageFormat.Webp)
        {
            using var codec = SKCodec.Create(inputPath);
            if (codec.Info.Width > WebpMaxDimension || codec.Info.Height > WebpMaxDimension)
            {
                int formatLimit = WebpMaxDimension;
                effectiveMaxDim = effectiveMaxDim.HasValue
                    ? Math.Min(effectiveMaxDim.Value, formatLimit)
                    : formatLimit;
                _logger.LogInformation("WebP auto-downscale: {Input} exceeds {Limit}px, limiting to {Limit}px", inputPath, WebpMaxDimension, WebpMaxDimension);
            }
        }

        return await Task.Run(async () =>
        {
            try
            {
                using var bitmap = SKBitmap.Decode(inputPath);
                if (bitmap == null)
                    throw new InvalidOperationException($"Failed to decode image: {inputPath}");
                progress?.Report(0.1);

                SKBitmap? resized = null;
                try
                {
                    if (effectiveMaxDim.HasValue)
                    {
                        if (bitmap.Width > effectiveMaxDim.Value || bitmap.Height > effectiveMaxDim.Value)
                        {
                            resized = ResizeToFit(bitmap, effectiveMaxDim.Value, filterType);
                            progress?.Report(0.4);
                        }
                    }
                    else if (scalePercent.HasValue && scalePercent.Value > 0 && scalePercent.Value < 100)
                    {
                        int newW = Math.Max(1, bitmap.Width * scalePercent.Value / 100);
                        int newH = Math.Max(1, bitmap.Height * scalePercent.Value / 100);
                        resized = ResizeExact(bitmap, newW, newH, filterType);
                        progress?.Report(0.4);
                    }

                    var src = resized ?? bitmap;

                    using var image = SKImage.FromBitmap(src);
                    using var data = image.Encode(outputFormat, quality);
                    progress?.Report(0.5);

                    byte[] encoded = data.ToArray();
                    await File.WriteAllBytesAsync(tmpPath, encoded, ct);
                    progress?.Report(0.9);

                    if (File.Exists(outputPath))
                        File.Delete(outputPath);
                    File.Move(tmpPath, outputPath);

                    _logger.LogInformation("Image converted: {Input} -> {Output} ({Format})", inputPath, outputPath, format);
                    progress?.Report(1.0);
                    return outputPath;
                }
                finally
                {
                    resized?.Dispose();
                }
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                _logger.LogWarning("Image conversion failed: {Input}", inputPath);
                throw;
            }
        }, ct);
    }

    public async Task<Bitmap?> LoadImageAsync(string imagePath, CancellationToken ct = default)
    {
        var fi = new FileInfo(imagePath);
        if (fi.Length > MaxImageSize)
            throw new InvalidOperationException($"Image too large ({fi.Length / 1024 / 1024}MB). Max: {MaxImageSize / 1024 / 1024}MB");

        return await Task.Run(() =>
        {
            using var src = SKBitmap.Decode(imagePath);
            if (src == null) return null;

            byte[] pngBytes = EncodePreviewPng(src);

            using var ms = new MemoryStream(pngBytes);
            return new Bitmap(ms);
        }, ct);
    }

    public async Task<byte[]> ImageToBytesAsync(string imagePath, CancellationToken ct = default)
    {
        var fi = new FileInfo(imagePath);
        if (fi.Length > MaxImageSize)
            throw new InvalidOperationException($"Image too large ({fi.Length / 1024 / 1024}MB). Max: {MaxImageSize / 1024 / 1024}MB");

        return await File.ReadAllBytesAsync(imagePath, ct);
    }

    private byte[] EncodePreviewPng(SKBitmap src)
    {
        var bitmap = src;
        bool shouldDispose = false;

        if (src.Width > PreviewMaxDimension || src.Height > PreviewMaxDimension)
        {
            float scale = Math.Min((float)PreviewMaxDimension / src.Width, (float)PreviewMaxDimension / src.Height);
            int newW = Math.Max(1, (int)(src.Width * scale));
            int newH = Math.Max(1, (int)(src.Height * scale));
            bitmap = ResizeExact(src, newW, newH, null);
            shouldDispose = true;
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] pngBytes = data.ToArray();

        if (shouldDispose) bitmap.Dispose();
        return pngBytes;
    }

    private static SKBitmap ResizeToFit(SKBitmap src, int maxDim, string? filterType)
    {
        float scale = Math.Min((float)maxDim / src.Width, (float)maxDim / src.Height);
        int newW = Math.Max(1, (int)(src.Width * scale));
        int newH = Math.Max(1, (int)(src.Height * scale));

        return ResizeExact(src, newW, newH, filterType);
    }

    private static SKBitmap ResizeExact(SKBitmap src, int width, int height, string? filterType)
    {
        var info = new SKImageInfo(width, height, src.ColorType, src.AlphaType);
        return src.Resize(info, MapSamplingOptions(filterType));
    }

    private static SKSamplingOptions MapSamplingOptions(string? filter)
    {
        if (string.IsNullOrEmpty(filter))
            return new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        return filter.ToLowerInvariant() switch
        {
            "lanczos" => new SKSamplingOptions(new SKCubicResampler(1.0f / 3, 1.0f / 3)),
            "mitchell" => new SKSamplingOptions(new SKCubicResampler(1.0f / 3, 1.0f / 3)),
            "catrom" => new SKSamplingOptions(SKCubicResampler.CatmullRom),
            "cubic" => new SKSamplingOptions(SKCubicResampler.CatmullRom),
            "triangle" => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
            "box" => new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
            _ => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
        };
    }

    private static (SKEncodedImageFormat Format, int Quality) MapFormat(string format) => format.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => (SKEncodedImageFormat.Jpeg, 90),
        "png" => (SKEncodedImageFormat.Png, 100),
        "gif" => (SKEncodedImageFormat.Gif, 100),
        "bmp" => (SKEncodedImageFormat.Bmp, 100),
        "webp" => (SKEncodedImageFormat.Webp, 100),
        "heic" => (SKEncodedImageFormat.Heif, 100),
        _ => throw new NotSupportedException($"Format '{format}' is not supported.")
    };
}
