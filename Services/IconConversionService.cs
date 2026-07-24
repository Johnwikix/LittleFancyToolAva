using ImageMagick;
using Microsoft.Extensions.Logging;

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
            using var image = new MagickImage(imagePath);

            uint side = Math.Min(image.Width, image.Height);
            image.Crop(side, side, Gravity.Center);
            image.Resize((uint)size, (uint)size);

            image.Format = MagickFormat.Ico;
            byte[] icoBytes = image.ToByteArray();

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
}
