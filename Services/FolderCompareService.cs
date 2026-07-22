using LittleFancyToolAva.Models;
using LittleFancyToolAva.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace LittleFancyToolAva.Services;

public class FolderCompareService : IFolderCompareService
{
    private readonly ILogger<FolderCompareService> _logger;

    public FolderCompareService(ILogger<FolderCompareService> logger)
    {
        _logger = logger;
    }

    public async Task<List<FolderCompareResult>> CompareFoldersAsync(
        string sourceFolder, string targetFolder,
        bool useHashComparison, bool useMusicTitleComparison,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        List<FolderCompareResult> results = [];

        if (!useHashComparison && !useMusicTitleComparison)
        {
            throw new InvalidOperationException("至少需要选择一种比较方式（哈希比较或音乐标题比较）");
        }

        var sourceFiles = new List<string>();
        var targetFiles = new List<string>();

        try
        {
            sourceFiles = Directory.EnumerateFiles(sourceFolder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            }).ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Cannot enumerate source folder: {Folder}", sourceFolder);
        }

        try
        {
            targetFiles = Directory.EnumerateFiles(targetFolder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            }).ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Cannot enumerate target folder: {Folder}", targetFolder);
        }

        cancellationToken.ThrowIfCancellationRequested();

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
        int skippedCount = 0;

        foreach (string sourceFile in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relPath = Path.GetRelativePath(sourceFolder, sourceFile);
            if (targetRelPathMap.TryGetValue(relPath, out string? targetFile))
            {
                bool match = true;
                string? sourceDetail = null;
                string? targetDetail = null;
                CompareState state = CompareState.Match;

                if (useHashComparison)
                {
                    try
                    {
                        string sourceHash = await Task.Run(() => ToolMethod.CalculateFileHash(sourceFile, "SHA256"), cancellationToken);
                        string targetHash = await Task.Run(() => ToolMethod.CalculateFileHash(targetFile, "SHA256"), cancellationToken);
                        sourceDetail = sourceHash;
                        targetDetail = targetHash;
                        match = string.Equals(sourceHash, targetHash, StringComparison.OrdinalIgnoreCase);
                        state = match ? CompareState.Match : CompareState.Different;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        state = CompareState.SourceOnly;
                        sourceDetail = "<access denied>";
                        skippedCount++;
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "Hash compare failed for {File}", relPath);
                        state = CompareState.SourceOnly;
                        sourceDetail = "<io error>";
                        skippedCount++;
                    }
                }

                if (match && useMusicTitleComparison)
                {
                    string sourceTitle = ExtractMusicTitle(sourceFile);
                    string targetTitle = ExtractMusicTitle(targetFile);
                    sourceDetail = sourceTitle;
                    targetDetail = targetTitle;
                    match = string.Equals(sourceTitle, targetTitle, StringComparison.OrdinalIgnoreCase);
                    state = match ? CompareState.Match : CompareState.Different;
                }

                results.Add(new FolderCompareResult
                {
                    RelativePath = relPath,
                    State = state,
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
            cancellationToken.ThrowIfCancellationRequested();

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

        if (skippedCount > 0)
        {
            _logger.LogWarning("Folder compare completed with {Skipped} inaccessible items", skippedCount);
        }

        _logger.LogInformation("Folder compare completed: {Results} results, {Skipped} skipped", results.Count, skippedCount);
        return results;
    }

    private string ExtractMusicTitle(string filePath)
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
        catch (TagLib.CorruptFileException ex)
        {
            _logger.LogDebug(ex, "Corrupt music file: {Path}", filePath);
            return Path.GetFileNameWithoutExtension(filePath);
        }
        catch (TagLib.UnsupportedFormatException ex)
        {
            _logger.LogDebug(ex, "Unsupported music format: {Path}", filePath);
            return Path.GetFileNameWithoutExtension(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read music title: {Path}", filePath);
            return Path.GetFileNameWithoutExtension(filePath);
        }
    }
}
