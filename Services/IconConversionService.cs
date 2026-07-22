using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace LittleFancyToolAva.Services;

public class IconConversionService : IIconConversionService
{
    private readonly ILogger<IconConversionService> _logger;

    public static readonly int[] AvailableSizes = [16, 32, 48, 64, 128, 256];

    public IconConversionService(ILogger<IconConversionService> logger)
    {
        _logger = logger;
    }

    public async Task<byte[]> CreateIcoBytesAsync(string imagePath, int size)
    {
        if (!AvailableSizes.Contains(size))
            throw new ArgumentException($"Icon size must be one of: {string.Join(", ", AvailableSizes)}");

        using Image image = await SixLabors.ImageSharp.Image.LoadAsync(imagePath);
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(size, size),
            Mode = ResizeMode.Crop
        }));

        using MemoryStream pngStream = new();
        await image.SaveAsPngAsync(pngStream, new PngEncoder());
        byte[] pngData = pngStream.ToArray();

        using MemoryStream icoStream = new();
        using BinaryWriter writer = new(icoStream);

        int count = 1;
        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)count);

        int dataOffset = 6 + 16 * count;
        byte iconWidth = size >= 256 ? (byte)0 : (byte)size;
        byte iconHeight = size >= 256 ? (byte)0 : (byte)size;

        writer.Write(iconWidth);
        writer.Write(iconHeight);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(pngData.Length);
        writer.Write(dataOffset);

        writer.Write(pngData);

        _logger.LogDebug("ICO created: {Size}x{Size} ({PngBytes} bytes)", size, size, pngData.Length);
        return icoStream.ToArray();
    }

    public async Task<bool> SaveAsIcoAsync(string imagePath, string outputPath, int size)
    {
        byte[] icoBytes = await CreateIcoBytesAsync(imagePath, size);
        string tmpPath = outputPath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tmpPath, icoBytes);
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
