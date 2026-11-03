using FancyToolAva.Models;
using FancyToolAva.Services.Lyrics;

namespace FancyToolAva.Cli.Commands;

internal static class LyricsCommands
{
    public static void Register(Dictionary<string, Func<CliContext, Task<int>>> map)
    {
        map["lyrics extract"] = ExtractAsync;
        map["lyrics export"] = ExportAsync;
    }

    private static async Task<int> ExtractAsync(CliContext ctx)
    {
        IReadOnlyList<string> inputs = ctx.Args.GetAll("--input");
        if (inputs.Count == 0) throw new CliUsageException(ctx.Zh ? "需要 -i/--input。" : "-i/--input is required.");
        string outputDirectory = ctx.Args.Require("--out", ctx.Zh ? "需要 -o/--out（输出目录）。" : "-o/--out (output directory) is required.");
        Directory.CreateDirectory(outputDirectory);

        var options = new LyricsOptions
        {
            Language = ctx.Args.Get("--language") ?? "Auto",
            Performance = ctx.Args.GetChoice("--performance", "automatic", "automatic", "resource-saving", "high-precision") switch
            {
                "resource-saving" => LyricsPerformance.ResourceSaving,
                "high-precision" => LyricsPerformance.HighPrecision,
                _ => LyricsPerformance.Automatic,
            },
            UseGpu = !ctx.Args.GetBool("--no-gpu"),
            SongMode = true,
            PreferExisting = !ctx.Args.GetBool("--no-prefer-existing"),
            OutputDirectory = outputDirectory,
            ModelDirectory = ctx.Args.Get("--model-dir") ?? string.Empty,
            Formats = ParseFormats(ctx.Args.Get("--formats") ?? "lrc,srt", ctx.Zh),
            ChunkSeconds = ctx.Args.GetInt("--chunk-seconds", 30),
            MaxLineCharacters = ctx.Args.GetInt("--max-line-chars", 42),
            OffsetMs = ctx.Args.GetInt("--offset-ms", 0),
        };

        var service = ctx.Get<LyricsExtractionService>();
        var results = new List<object>();
        int failed = 0;
        foreach (string input in inputs)
        {
            if (!File.Exists(input))
            {
                failed++;
                ctx.Error((ctx.Zh ? "找不到文件: " : "File not found: ") + input);
                continue;
            }
            try
            {
                var progress = new Progress<LyricsProgress>(p => PrintProgress(ctx, p));
                LyricsDocument document = await service.ExtractAsync(input, options, progress, ctx.CancellationToken)
                    .ConfigureAwait(false);
                ctx.ProgressDone();
                string basePath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(input));
                string project = await LyricsFileService.ExportAsync(document, basePath, options.Formats, options.OffsetMs, ctx.CancellationToken)
                    .ConfigureAwait(false);
                results.Add(new
                {
                    source = input,
                    project,
                    language = document.Language,
                    durationMs = document.DurationMs,
                    lines = document.Lines.Count,
                    formats = options.Formats.ToString(),
                    warnings = document.Warnings,
                    success = true,
                });
                if (!ctx.Json) ctx.WriteResult(project);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                ctx.ProgressDone();
                ctx.Error((ctx.Zh ? "提取失败: " : "Extraction failed: ") + input + " - " + ex.Message);
                results.Add(new { source = input, success = false, error = ex.Message });
            }
        }

        if (ctx.Json && inputs.Count > 1) ctx.WriteJson(new { failed, results });
        else if (ctx.Json && results.Count == 1) ctx.WriteJson(results[0]);
        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> ExportAsync(CliContext ctx)
    {
        string input = ctx.Args.Require("--input", ctx.Zh ? "需要 -i/--input。" : "-i/--input is required.");
        string outputBase = ctx.Args.Require("--out", ctx.Zh ? "需要 -o/--out（输出基名）。" : "-o/--out (output base path) is required.");
        if (!File.Exists(input)) throw new FileNotFoundException(input);
        string source = ctx.Args.Get("--source") ?? input;
        int duration = ctx.Args.GetInt("--duration-ms", 0);
        LyricsFormats formats = ParseFormats(ctx.Args.Get("--formats") ?? "lrc,srt", ctx.Zh);
        LyricsDocument document = LyricsFileService.Import(input, source, duration);
        int offset = ctx.Args.GetInt("--offset-ms", 0);
        string project = await LyricsFileService.ExportAsync(document, outputBase, formats, offset, ctx.CancellationToken)
            .ConfigureAwait(false);
        if (ctx.Json) ctx.WriteJson(new { input, project, formats = formats.ToString() });
        else ctx.WriteResult(project);
        return 0;
    }

    private static void PrintProgress(CliContext ctx, LyricsProgress progress)
    {
        if (ctx.Quiet || ctx.Json) return;
        ctx.Progress(progress.Fraction);
        if (progress.Fraction >= 1) ctx.ProgressDone();
        else if (!string.IsNullOrEmpty(progress.Detail) && !Console.IsErrorRedirected)
            Console.Error.Write($"\r{progress.Stage} {progress.Fraction:P0} {progress.Detail}      ");
    }

    private static LyricsFormats ParseFormats(string value, bool zh)
    {
        LyricsFormats result = 0;
        foreach (string raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result |= raw.ToLowerInvariant() switch
            {
                "lrc" => LyricsFormats.Lrc,
                "elrc" or "enhanced-lrc" => LyricsFormats.EnhancedLrc,
                "krc" => LyricsFormats.Krc,
                "qrc" => LyricsFormats.Qrc,
                "srt" => LyricsFormats.Srt,
                "vtt" => LyricsFormats.Vtt,
                "ass" => LyricsFormats.Ass,
                "yrc" => LyricsFormats.Yrc,
                "txt" or "text" => LyricsFormats.Text,
                "json" => LyricsFormats.Json,
                _ => throw new CliUsageException((zh ? "未知歌词格式: " : "Unknown lyrics format: ") + raw),
            };
        }
        if (result == 0) throw new CliUsageException(zh ? "至少需要一种歌词格式。" : "At least one lyrics format is required.");
        return result;
    }
}