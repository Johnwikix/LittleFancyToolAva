namespace LittleFancyToolAva.Services;

public interface IIconConversionService
{
    Task<byte[]> CreateIcoBytesAsync(string imagePath, int size);
    Task<bool> SaveAsIcoAsync(string imagePath, string outputPath, int size);
}
