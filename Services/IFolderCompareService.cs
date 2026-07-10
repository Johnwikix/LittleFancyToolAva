using LittleFancyToolAva.Models;

namespace LittleFancyToolAva.Services;

public interface IFolderCompareService
{
    Task<List<FolderCompareResult>> CompareFoldersAsync(
        string sourceFolder, string targetFolder,
        bool useHashComparison, bool useMusicTitleComparison,
        IProgress<double>? progress = null);
}
