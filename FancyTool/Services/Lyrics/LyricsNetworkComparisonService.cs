using ATL;
using FancyToolAva.Models;
using Lyricify.Lyrics.Providers.Web.Netease;
using Lyricify.Lyrics.Providers.Web.QQMusic;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using Lyricify.Lyrics.Models;

namespace FancyToolAva.Services.Lyrics;

/// <summary>
/// Network lyrics are deliberately isolated from production extraction. This is
/// a comparison oracle for QA: it can show what public lyric sources return for
/// the same audio metadata, without replacing Qwen's recognition result.
/// </summary>
public sealed record NetworkLyricsComparison(string Provider, string Title, string Artist, string Match, LyricsDocument Document);

public sealed class LyricsNetworkComparisonService
{
    public List<string> Diagnostics { get; } = [];
    public async Task<IReadOnlyList<NetworkLyricsComparison>> FetchAsync(string source, CancellationToken ct)
    {
        var track = new Track(source);
        string stem = Path.GetFileNameWithoutExtension(source);
        string? fileArtist = null, fileTitle = null;
        int separator = stem.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0) { fileArtist = stem[..separator].Trim(); fileTitle = stem[(separator + 3)..].Trim(); }
        var metadata = new TrackMetadata
        {
            Title = string.IsNullOrWhiteSpace(track.Title) ? fileTitle ?? stem : track.Title,
            Artist = string.IsNullOrWhiteSpace(track.Artist) ? fileArtist : track.Artist,
            Album = track.Album,
            AlbumArtist = track.AlbumArtist,
            DurationMs = (int)Math.Clamp(track.DurationMs, 0, int.MaxValue)
        };
        var results = new List<NetworkLyricsComparison>();
        Diagnostics.Add($"metadata={metadata.Artist} - {metadata.Title} duration={metadata.DurationMs}");
        await TryNetease(metadata, source, results, ct).ConfigureAwait(false);
        await TryQqMusic(metadata, source, results, ct).ConfigureAwait(false);
        return results;
    }

    private async Task TryNetease(TrackMetadata metadata, string source, List<NetworkLyricsComparison> results, CancellationToken ct)
    {
        try
        {
            var searcher = new NeteaseSearcher();
            var hit = await searcher.SearchForResult(metadata, CompareHelper.MatchType.PrettyHigh, ct).ConfigureAwait(false);
            if (hit is not NeteaseSearchResult result) return;
            var response = await new Lyricify.Lyrics.Providers.Web.Netease.Api().GetLyric(result.Id, ct).ConfigureAwait(false);
            string? text = response?.Yrc?.Lyric ?? response?.Lrc?.Lyric;
            if (string.IsNullOrWhiteSpace(text)) return;
            string extension = response?.Yrc?.Lyric is { Length: > 0 } ? ".yrc" : ".lrc";
            var document = LyricsFileService.ImportText(text, extension, source, metadata.DurationMs ?? 0);
            document.Model = "Lyricify network comparison";
            document.Backend = "Network: Netease";
            results.Add(new("Netease", result.Title, string.Join(", ", result.Artists), result.MatchType?.ToString() ?? "Unknown", document));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Add("Netease: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private async Task TryQqMusic(TrackMetadata metadata, string source, List<NetworkLyricsComparison> results, CancellationToken ct)
    {
        try
        {
            var searcher = new QQMusicSearcher();
            var hit = await searcher.SearchForResult(metadata, CompareHelper.MatchType.PrettyHigh, ct).ConfigureAwait(false);
            if (hit is not QQMusicSearchResult result) return;
            var response = await new Lyricify.Lyrics.Providers.Web.QQMusic.Api().GetLyricsAsync(result.Id, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response?.Lyrics)) return;
            var document = LyricsFileService.ImportText(response.Lyrics, ".qrc", source, metadata.DurationMs ?? 0);
            document.Model = "Lyricify network comparison";
            document.Backend = "Network: QQ Music";
            results.Add(new("QQ Music", result.Title, string.Join(", ", result.Artists), result.MatchType?.ToString() ?? "Unknown", document));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Add("QQ Music: " + ex.GetType().Name + ": " + ex.Message); }
    }
}
