using Avalonia.Media.Imaging;
using ImageMagick;
using Microsoft.Extensions.Logging;

namespace LittleFancyToolAva.Services;

public class ImageConversionService : IImageConversionService
{
    private const long MaxImageSize = 1024 * 1024 * 1024;
    private const int PreviewMaxDimension = 1920;
    private const int StripHeight = 1024;
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
            using var image = new MagickImage(bytes);
            if (image.Width > PreviewMaxDimension || image.Height > PreviewMaxDimension)
                image.Resize((uint)PreviewMaxDimension, (uint)PreviewMaxDimension);

            byte[] pngBytes = image.ToByteArray(MagickFormat.Png);
            using var ms = new MemoryStream(pngBytes);
            return new Bitmap(ms);
        }, ct);
    }

    public async Task<string?> ConvertImageFormatAsync(string inputPath, string outputPath, string format, CancellationToken ct = default, int? maxDimension = null, string? filterType = null, IProgress<double>? progress = null, int? scalePercent = null)
    {
        string tmpPath = outputPath + ".tmp";
        MagickFormat outputFormat = MapFormat(format);

        int? effectiveMaxDim = maxDimension;

        if (outputFormat == MagickFormat.WebP)
        {
            var info = new MagickImageInfo(inputPath);
            if (info.Width > WebpMaxDimension || info.Height > WebpMaxDimension)
            {
                int formatLimit = WebpMaxDimension;
                effectiveMaxDim = effectiveMaxDim.HasValue
                    ? Math.Min(effectiveMaxDim.Value, formatLimit)
                    : formatLimit;
                _logger.LogInformation("WebP auto-downscale: {Input} exceeds {Limit}px, limiting to {Limit}px", inputPath, WebpMaxDimension, WebpMaxDimension);
            }
        }

        if (outputFormat == MagickFormat.Bmp && !effectiveMaxDim.HasValue)
            return await ConvertToBmpStripwiseAsync(inputPath, tmpPath, outputPath, ct, progress);

        return await Task.Run(() =>
        {
            try
            {
                using var image = new MagickImage(inputPath);
                progress?.Report(0.1);

                if (effectiveMaxDim.HasValue)
                {
                    if (image.Width > effectiveMaxDim.Value || image.Height > effectiveMaxDim.Value)
                    {
                        ImageMagick.FilterType ft = MapFilterType(filterType);
                        image.FilterType = ft;
                        image.Resize((uint)effectiveMaxDim.Value, (uint)effectiveMaxDim.Value);
                        progress?.Report(0.4);
                    }
                }
                else if (scalePercent.HasValue && scalePercent.Value > 0 && scalePercent.Value < 100)
                {
                    ImageMagick.FilterType ft = MapFilterType(filterType);
                    image.FilterType = ft;
                    int newW = Math.Max(1, (int)image.Width * scalePercent.Value / 100);
                    int newH = Math.Max(1, (int)image.Height * scalePercent.Value / 100);
                    image.Resize((uint)newW, (uint)newH);
                    progress?.Report(0.4);
                }

                image.Format = outputFormat;
                if (outputFormat is MagickFormat.Jpeg or MagickFormat.Jpg)
                    image.Quality = 90;

                progress?.Report(0.5);
                image.Write(tmpPath);
                progress?.Report(0.9);

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
        }, ct);
    }

    private async Task<string?> ConvertToBmpStripwiseAsync(string inputPath, string tmpPath, string outputPath, CancellationToken ct, IProgress<double>? progress = null)
    {
        return await Task.Run(() =>
        {
            try
            {
                var info = new MagickImageInfo(inputPath);
                int width = (int)info.Width;
                int height = (int)info.Height;

                var fi = new FileInfo(inputPath);
                if (fi.Length > MaxImageSize)
                    throw new InvalidOperationException($"Image too large ({fi.Length / 1024 / 1024}MB). Max: {MaxImageSize / 1024 / 1024}MB");

                progress?.Report(0.05);

                using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write);
                WriteBmpHeader(fs, width, height);

                int rowSize = width * 4;
                byte[] rowBuffer = new byte[rowSize];
                int totalStrips = (height + StripHeight - 1) / StripHeight;
                int stripIndex = 0;

                int y = 0;
                while (y < height)
                {
                    ct.ThrowIfCancellationRequested();

                    int stripH = Math.Min(StripHeight, height - y);
                    stripIndex++;
                    var settings = new MagickReadSettings
                    {
                        ExtractArea = new MagickGeometry(0, y, (uint)width, (uint)stripH)
                    };

                    using var strip = new MagickImage(inputPath, settings);

                    using var stripMs = new MemoryStream();
                    strip.Write(stripMs, MagickFormat.Bmp);
                    byte[] stripBytes = stripMs.ToArray();

                    int stripPixelOffset = 54;
                    int stripPixelCount = stripBytes.Length - stripPixelOffset;

                    for (int i = 0; i < stripH / 2; i++)
                    {
                        int src = i * rowSize;
                        int dst = (stripH - 1 - i) * rowSize;
                        Array.Copy(stripBytes, stripPixelOffset + src, rowBuffer, 0, rowSize);
                        Array.Copy(stripBytes, stripPixelOffset + dst, stripBytes, stripPixelOffset + src, rowSize);
                        Array.Copy(rowBuffer, 0, stripBytes, stripPixelOffset + dst, rowSize);
                    }

                    fs.Write(stripBytes, stripPixelOffset, stripPixelCount);
                    y += stripH;
                    progress?.Report(0.05 + 0.9 * stripIndex / totalStrips);
                }

                fs.Close();

                if (File.Exists(outputPath))
                    File.Delete(outputPath);
                File.Move(tmpPath, outputPath);

                _logger.LogInformation("Image converted (BMP strip-wise): {Input} -> {Output}", inputPath, outputPath);
                return outputPath;
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
        }, ct);
    }

    private static void WriteBmpHeader(FileStream fs, int width, int height)
    {
        int rowSize = width * 4;
        int pixelDataSize = rowSize * height;
        const int headerSize = 54;
        int fileSize = headerSize + pixelDataSize;

        fs.WriteByte(0x42);
        fs.WriteByte(0x4D);
        fs.Write(BitConverter.GetBytes(fileSize));
        fs.Write(BitConverter.GetBytes((short)0));
        fs.Write(BitConverter.GetBytes((short)0));
        fs.Write(BitConverter.GetBytes(headerSize));
        fs.Write(BitConverter.GetBytes(40));
        fs.Write(BitConverter.GetBytes(width));
        fs.Write(BitConverter.GetBytes(-height));
        fs.Write(BitConverter.GetBytes((short)1));
        fs.Write(BitConverter.GetBytes((short)32));
        fs.Write(BitConverter.GetBytes(0));
        fs.Write(BitConverter.GetBytes(pixelDataSize));
        fs.Write(BitConverter.GetBytes(0));
        fs.Write(BitConverter.GetBytes(0));
        fs.Write(BitConverter.GetBytes(0));
        fs.Write(BitConverter.GetBytes(0));
    }

    public async Task<Bitmap?> LoadImageAsync(string imagePath, CancellationToken ct = default)
    {
        var fi = new FileInfo(imagePath);
        if (fi.Length > MaxImageSize)
            throw new InvalidOperationException($"Image too large ({fi.Length / 1024 / 1024}MB). Max: {MaxImageSize / 1024 / 1024}MB");

        return await Task.Run(() =>
        {
            using var image = new MagickImage(imagePath);
            if (image.Width > PreviewMaxDimension || image.Height > PreviewMaxDimension)
                image.Resize((uint)PreviewMaxDimension, (uint)PreviewMaxDimension);

            byte[] pngBytes = image.ToByteArray(MagickFormat.Png);
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

    private static MagickFormat MapFormat(string format) => format.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => MagickFormat.Jpeg,
        "png" => MagickFormat.Png,
        "gif" => MagickFormat.Gif,
        "bmp" => MagickFormat.Bmp,
        "webp" => MagickFormat.WebP,
        "tiff" => MagickFormat.Tiff,
        _ => throw new NotSupportedException($"Format '{format}' is not supported.")
    };

    private static ImageMagick.FilterType MapFilterType(string? filter) => filter?.ToLowerInvariant() switch
    {
        "lanczos" => ImageMagick.FilterType.Lanczos,
        "mitchell" => ImageMagick.FilterType.Mitchell,
        "catrom" => ImageMagick.FilterType.Catrom,
        "cubic" => ImageMagick.FilterType.Cubic,
        "triangle" => ImageMagick.FilterType.Triangle,
        "box" => ImageMagick.FilterType.Box,
        _ => ImageMagick.FilterType.Lanczos
    };
}
