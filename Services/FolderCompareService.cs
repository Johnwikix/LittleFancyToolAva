using LittleFancyToolAva.Models;
using LittleFancyToolAva.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace LittleFancyToolAva.Services;

public class FolderCompareService : IFolderCompareService
{
    private const int MaxHashParallelism = 8;

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
        if (!useHashComparison && !useMusicTitleComparison)
        {
            throw new InvalidOperationException(LocalizationRegistry.Get("FolderCompare.Service_NeedCompareMode"));
        }

        var sourceFiles = SafeEnumerate(sourceFolder, _logger);
        var targetFiles = SafeEnumerate(targetFolder, _logger);

        cancellationToken.ThrowIfCancellationRequested();

        var targetRelPathMap = targetFiles.ToDictionary(
            f => Path.GetRelativePath(targetFolder, f),
            f => f,
            StringComparer.OrdinalIgnoreCase);

        var sourceRelPathMap = sourceFiles.ToDictionary(
            f => Path.GetRelativePath(sourceFolder, f),
            f => f,
            StringComparer.OrdinalIgnoreCase);

        List<FolderCompareResult> results = new(sourceFiles.Count + targetFiles.Count);
        int processed = 0;
        int totalItems = sourceFiles.Count + targetFiles.Count;
        int skippedCount = 0;

        await Parallel.ForEachAsync(sourceFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = useHashComparison ? Math.Min(MaxHashParallelism, Environment.ProcessorCount) : 1,
                CancellationToken = cancellationToken
            },
            async (sourceFile, ct) =>
            {
                string relPath = Path.GetRelativePath(sourceFolder, sourceFile);
                if (!targetRelPathMap.TryGetValue(relPath, out string? targetFile))
                {
                    lock (results)
                    {
                        results.Add(new FolderCompareResult
                        {
                            RelativePath = relPath,
                            State = CompareState.SourceOnly,
                            SourceDetail = "Only in source"
                        });
                    }
                    ReportProgress(progress, ref processed, totalItems);
                    return;
                }

                bool match = true;
                string? sourceDetail = null;
                string? targetDetail = null;
                CompareState state = CompareState.Match;

                if (useHashComparison)
                {
                    try
                    {
                        string sourceHash = await Task.Run(() => ToolMethod.CalculateFileHash(sourceFile, "SHA256"), ct);
                        string targetHash = await Task.Run(() => ToolMethod.CalculateFileHash(targetFile, "SHA256"), ct);
                        sourceDetail = sourceHash;
                        targetDetail = targetHash;
                        match = string.Equals(sourceHash, targetHash, StringComparison.OrdinalIgnoreCase);
                        state = match ? CompareState.Match : CompareState.Different;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        state = CompareState.SourceOnly;
                        sourceDetail = "<access denied>";
                        Interlocked.Increment(ref skippedCount);
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "Hash compare failed for {File}", relPath);
                        state = CompareState.SourceOnly;
                        sourceDetail = "<io error>";
                        Interlocked.Increment(ref skippedCount);
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

                lock (results)
                {
                    results.Add(new FolderCompareResult
                    {
                        RelativePath = relPath,
                        State = state,
                        SourceDetail = sourceDetail,
                        TargetDetail = targetDetail
                    });
                }
                ReportProgress(progress, ref processed, totalItems);
            });

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

    private static List<string> SafeEnumerate(string folder, ILogger logger)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            }).ToList();
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Cannot enumerate folder: {Folder}", folder);
            return [];
        }
    }

    private static void ReportProgress(IProgress<double>? progress, ref int counter, int total)
    {
        int p = Interlocked.Increment(ref counter);
        progress?.Report((double)p / total);
    }

    private string ExtractMusicTitle(string filePath)
    {
        if (!ToolMethod.IsMusicFile(filePath))
            return Path.GetFileNameWithoutExtension(filePath);

        try
        {
            string title = new ATL.Track(filePath).Title;
            return string.IsNullOrEmpty(title)
                ? Path.GetFileNameWithoutExtension(filePath)
                : title;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read music title: {Path}", filePath);
            return Path.GetFileNameWithoutExtension(filePath);
        }
    }
}
