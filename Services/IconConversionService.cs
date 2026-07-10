using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace LittleFancyToolAva.Services;

public class IconConversionService : IIconConversionService
{
    public async Task<byte[]> CreateIcoBytesAsync(string imagePath, int size)
    {
        using Image image = await SixLabors.ImageSharp.Image.LoadAsync(imagePath);
        image.Mutate(x => x.Resize(size, size));

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

        return icoStream.ToArray();
    }

    public async Task<bool> SaveAsIcoAsync(string imagePath, string outputPath, int size)
    {
        byte[] icoBytes = await CreateIcoBytesAsync(imagePath, size);
        await File.WriteAllBytesAsync(outputPath, icoBytes);
        return true;
    }
}
