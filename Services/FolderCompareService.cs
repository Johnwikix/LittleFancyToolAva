using LittleFancyToolAva.Models;
using LittleFancyToolAva.Utils;
using System.Collections.ObjectModel;

namespace LittleFancyToolAva.Services;

public class FolderCompareService : IFolderCompareService
{
    public async Task<List<FolderCompareResult>> CompareFoldersAsync(
        string sourceFolder, string targetFolder,
        bool useHashComparison, bool useMusicTitleComparison,
        IProgress<double>? progress = null)
    {
        List<FolderCompareResult> results = [];
        List<string> sourceFiles = Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories).ToList();
        List<string> targetFiles = Directory.GetFiles(targetFolder, "*", SearchOption.AllDirectories).ToList();

        var targetRelPathMap = targetFiles.ToDictionary(
            f => Path.GetRelativePath(targetFolder, f),
            f => f,
            StringComparer.OrdinalIgnoreCase);

        var sourceRelPathMap = sourceFiles.ToDictionary(
            f => Path.GetRelativePath(sourceFolder, f),
            f => f,
            StringComparer.OrdinalIgnoreCase);

        int totalItems = sourceFiles.Count + targetFiles.Count;
        int processed = 0;

        foreach (string sourceFile in sourceFiles)
        {
            string relPath = Path.GetRelativePath(sourceFolder, sourceFile);
            if (targetRelPathMap.TryGetValue(relPath, out string? targetFile))
            {
                bool match = true;
                string? sourceDetail = null;
                string? targetDetail = null;

                if (useHashComparison)
                {
                    string sourceHash = await Task.Run(() => ToolMethod.CalculateFileHash(sourceFile, "MD5"));
                    string targetHash = await Task.Run(() => ToolMethod.CalculateFileHash(targetFile, "MD5"));
                    sourceDetail = sourceHash;
                    targetDetail = targetHash;
                    match = string.Equals(sourceHash, targetHash, StringComparison.OrdinalIgnoreCase);
                }

                if (match && useMusicTitleComparison)
                {
                    string sourceTitle = ExtractMusicTitle(sourceFile);
                    string targetTitle = ExtractMusicTitle(targetFile);
                    sourceDetail = sourceTitle;
                    targetDetail = targetTitle;
                    match = string.Equals(sourceTitle, targetTitle, StringComparison.OrdinalIgnoreCase);
                }

                results.Add(new FolderCompareResult
                {
                    RelativePath = relPath,
                    State = match ? CompareState.Match : CompareState.Different,
                    SourceDetail = sourceDetail,
                    TargetDetail = targetDetail
                });
            }
            else
            {
                results.Add(new FolderCompareResult
                {
                    RelativePath = relPath,
                    State = CompareState.SourceOnly,
                    SourceDetail = "Only in source"
                });
            }
            processed++;
            progress?.Report((double)processed / totalItems);
        }

        foreach (string targetFile in targetFiles)
        {
            string relPath = Path.GetRelativePath(targetFolder, targetFile);
            if (!sourceRelPathMap.ContainsKey(relPath))
            {
                results.Add(new FolderCompareResult
                {
                    RelativePath = relPath,
                    State = CompareState.TargetOnly,
                    TargetDetail = "Only in target"
                });
                processed++;
                progress?.Report((double)processed / totalItems);
            }
        }

        progress?.Report(1.0);
        return results;
    }

    private static string ExtractMusicTitle(string filePath)
    {
        if (!ToolMethod.IsMusicFile(filePath))
            return Path.GetFileNameWithoutExtension(filePath);

        try
        {
            using TagLib.File tagFile = TagLib.File.Create(filePath);
            return string.IsNullOrEmpty(tagFile.Tag.Title)
                ? Path.GetFileNameWithoutExtension(filePath)
                : tagFile.Tag.Title;
        }
        catch
        {
            return Path.GetFileNameWithoutExtension(filePath);
        }
    }
}
