using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace FancyToolAva.Services;

public class ImageConversionService : IImageConversionService
{
    private const long MaxImageSize = 1024 * 1024 * 1024;
    private const int PreviewMaxDimension = 1920;
    private const int WebpMaxDimension = 16383;
    private readonly ILogger<ImageConversionService> _logger;

    public static readonly int[] AvailableIcoSizes = [16, 32, 48, 64, 128, 256];

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

    public async Task<string?> ConvertImageFormatAsync(
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
        ISuperResolutionService? superResolutionService = null)
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

        if (superResolutionModel.HasValue && superResolutionService != null)
        {
            using var codec = SKCodec.Create(inputPath);
            if (codec == null)
                throw new InvalidOperationException($"Failed to read image: {inputPath}");
            int srTarget = superResolutionScale == 2 ? 2 : 4;
            long outW = (long)codec.Info.Width * srTarget;
            long outH = (long)codec.Info.Height * srTarget;
            if (outW > SuperResolutionService.MaxOutputDimension || outH > SuperResolutionService.MaxOutputDimension)
            {
                throw new SuperResolutionOutputTooLargeException(
                    (int)Math.Min(outW, int.MaxValue),
                    (int)Math.Min(outH, int.MaxValue),
                    SuperResolutionService.MaxOutputDimension);
            }
        }

return await Task.Run(async () =>
        {
            SKBitmap? original = null;
            SKBitmap? resized = null;
            SKBitmap? srBitmap = null;
            try
            {
                original = SKBitmap.Decode(inputPath);
                if (original == null)
                    throw new InvalidOperationException($"Failed to decode image: {inputPath}");
                progress?.Report(0.05);

                SKBitmap workingBitmap = original;

                if (superResolutionModel.HasValue && superResolutionService != null)
                {
                    int srTarget = superResolutionScale == 2 ? 2 : 4;
                    srBitmap = await superResolutionService.UpscaleAsync(workingBitmap, superResolutionModel.Value, srTarget,
                        new Progress<double>(p => progress?.Report(0.05 + p * 0.55)), ct);
                    workingBitmap = srBitmap;
                    _logger.LogInformation("Super-resolution applied: {Input} -> {W}x{H} ({Scale}x, model={Model})",
                        inputPath, workingBitmap.Width, workingBitmap.Height, srTarget, superResolutionModel.Value);
                }

                if (effectiveMaxDim.HasValue)
                {
                    if (workingBitmap.Width > effectiveMaxDim.Value || workingBitmap.Height > effectiveMaxDim.Value)
                    {
                        resized = ResizeToFit(workingBitmap, effectiveMaxDim.Value, filterType);
                        progress?.Report(0.7);
                    }
                }
                else if (scalePercent.HasValue && scalePercent.Value > 0 && scalePercent.Value < 100)
                {
                    int newW = Math.Max(1, workingBitmap.Width * scalePercent.Value / 100);
                    int newH = Math.Max(1, workingBitmap.Height * scalePercent.Value / 100);
                    resized = ResizeExact(workingBitmap, newW, newH, filterType);
                    progress?.Report(0.7);
                }

                var src = resized ?? workingBitmap;

                using var image = SKImage.FromBitmap(src);
                using var data = image.Encode(outputFormat, quality);
                progress?.Report(0.85);

                byte[] encoded = data.ToArray();
                await File.WriteAllBytesAsync(tmpPath, encoded, ct);
                progress?.Report(0.95);

                if (File.Exists(outputPath))
                    File.Delete(outputPath);
                File.Move(tmpPath, outputPath);

                _logger.LogInformation("Image converted: {Input} -> {Output} ({Format})", inputPath, outputPath, format);
                progress?.Report(1.0);
                return outputPath;
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                _logger.LogWarning("Image conversion failed: {Input}", inputPath);
                throw;
            }
            finally
            {
                resized?.Dispose();
                srBitmap?.Dispose();
                original?.Dispose();
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

    public async Task<byte[]> CreateIcoBytesAsync(string imagePath, int size, CancellationToken ct = default)
    {
        if (!AvailableIcoSizes.Contains(size))
            throw new ArgumentException($"Icon size must be one of: {string.Join(", ", AvailableIcoSizes)}");

        return await Task.Run(() =>
        {
            using var src = SKBitmap.Decode(imagePath);
            if (src == null)
                throw new InvalidOperationException($"Failed to decode image: {imagePath}");

            uint side = (uint)Math.Min(src.Width, src.Height);
            int x = (src.Width - (int)side) / 2;
            int y = (src.Height - (int)side) / 2;

            var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(src, new SKRect(x, y, x + side, y + side), new SKRect(0, 0, size, size));

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            byte[] pngBytes = data.ToArray();
            byte[] icoBytes = CreateIcoBytesFromPng(pngBytes, size);

            _logger.LogDebug("ICO created: {Size}x{Size} ({Bytes} bytes)", size, size, icoBytes.Length);
            return icoBytes;
        }, ct);
    }

    public async Task<bool> SaveAsIcoAsync(string imagePath, string outputPath, int size, CancellationToken ct = default)
    {
        byte[] icoBytes = await CreateIcoBytesAsync(imagePath, size, ct);
        string tmpPath = outputPath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tmpPath, icoBytes, ct);
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            File.Move(tmpPath, outputPath);
            _logger.LogInformation("ICO saved: {Output} ({Size}x{Size})", outputPath, size, size);
            return true;
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            _logger.LogWarning("ICO save failed: {Output}", outputPath);
            throw;
        }
    }

    private static byte[] CreateIcoBytesFromPng(byte[] pngBytes, int size)
    {
        byte b = size >= 256 ? (byte)0 : (byte)size;
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(b);
        writer.Write(b);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(pngBytes.Length);
        writer.Write(22);
        writer.Write(pngBytes);
        return ms.ToArray();
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
