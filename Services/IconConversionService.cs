using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace LittleFancyToolAva.Services;

public class IconConversionService : IIconConversionService
{
    private readonly ILogger<IconConversionService> _logger;

    public static readonly int[] AvailableSizes = [16, 32, 48, 64, 128, 256];

    public IconConversionService(ILogger<IconConversionService> logger)
    {
        _logger = logger;
    }

    public async Task<byte[]> CreateIcoBytesAsync(string imagePath, int size, CancellationToken ct = default)
    {
        if (!AvailableSizes.Contains(size))
            throw new ArgumentException($"Icon size must be one of: {string.Join(", ", AvailableSizes)}");

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
}
