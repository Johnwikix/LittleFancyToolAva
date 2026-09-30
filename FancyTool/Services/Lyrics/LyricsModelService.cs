using System.Text.Json;
using FancyToolAva.Models;

namespace FancyToolAva.Services.Lyrics;

public sealed class LyricsModelService(ModelDownloadService downloader)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _validated = new(StringComparer.OrdinalIgnoreCase);
    public static string DefaultDirectory => Environment.GetEnvironmentVariable("FANCYTOOL_LYRICS_MODELS")
        ?? Path.Combine(AppPaths.ModelsDirectory, "Qwen3Lyrics");
    public async Task EnsureAsync(string directory, bool highPrecision, IProgress<LyricsProgress>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (string pack in new[] { highPrecision ? "asr-fp32" : "asr", "aligner" })
            {
                string target = Path.Combine(directory, pack);
                var entries = GetManifest(pack);
                if (_validated.Contains(target) && entries.All(e => File.Exists(Path.Combine(target, e.FileName)))) continue;
                var report = new Progress<ModelDownloadProgress>(p => progress?.Report(new LyricsProgress("Lyrics.Stage_Download",
                    p.TotalBytes is > 0 ? (double)p.BytesDownloaded / p.TotalBytes.Value : 0,
                    $"{pack}/{p.FileName} {p.Stage} {p.Message}")));
                await downloader.EnsureModelsAsync(entries, target, report, ct).ConfigureAwait(false);
                _validated.Add(target);
            }
        }
        finally { _gate.Release(); }
    }
    public static IReadOnlyList<ModelManifestEntry> GetManifest(string pack)
    {
        using var stream = typeof(LyricsModelService).Assembly.GetManifestResourceStream("FancyToolAva.Resources.LyricsModels." + pack + ".json")
            ?? throw new InvalidOperationException("Missing model manifest: " + pack);
        return JsonSerializer.Deserialize(stream, LyricsJsonContext.Default.ListModelManifestEntry)!;
    }
    public async Task EnsureAlignerAsync(string directory, IProgress<LyricsProgress>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string target = Path.Combine(directory, "aligner");
            if (_validated.Contains(target) && GetManifest("aligner").All(e => File.Exists(Path.Combine(target, e.FileName)))) return;
            await downloader.EnsureModelsAsync(GetManifest("aligner"), target,
                new Progress<ModelDownloadProgress>(p => progress?.Report(new("Lyrics.Stage_Download", 0, p.FileName + " " + p.Stage))), ct).ConfigureAwait(false);
            _validated.Add(target);
        }
        finally { _gate.Release(); }
    }
    public void Invalidate() => _validated.Clear();
}
