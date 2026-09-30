using System.Text;
using System.Text.RegularExpressions;
using FancyToolAva.Models;

namespace FancyToolAva.Services.Lyrics;

public sealed class LyricsExtractionService(LyricsModelService models, PcmAudioDecoder decoder)
{
    public static readonly string[] Languages = ["Auto", "Chinese", "English", "Japanese", "Korean", "French", "Spanish", "Russian", "German", "Portuguese",
        "Arabic", "Cantonese", "Italian", "Indonesian", "Vietnamese", "Thai", "Turkish", "Hindi", "Malay", "Dutch", "Swedish", "Danish", "Finnish", "Polish", "Czech", "Filipino", "Persian", "Greek", "Romanian", "Hungarian", "Macedonian"];
    private static readonly HashSet<string> AlignmentLanguages = new(StringComparer.OrdinalIgnoreCase)
        { "Chinese", "English", "Japanese", "Korean", "French", "Spanish", "Russian", "German", "Portuguese", "Italian", "Cantonese" };
    private sealed record Chunk(int Start, int Length, int Left, int Right, string Text, string Language);

    public Task<LyricsDocument> ExtractAsync(string source, LyricsOptions options, IProgress<LyricsProgress>? progress, CancellationToken ct,
        Func<CancellationToken, Task>? waitIfPaused = null, int testLimitSeconds = 0)
        => Task.Run(async () =>
        {
            Validate(options);
            string cache = Path.Combine(Path.GetTempPath(), "FancyToolLyrics"); Directory.CreateDirectory(cache);
            string pcm = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".pcm");
            try
            {
                LyricsDocument? existing = options.PreferExisting ? FindExisting(source, options.Language) : null;
                if (existing != null && ((options.Formats & LyricsFileService.WordFormats) == 0 || existing.Lines.All(l => l.Words.Count > 0))) return existing;
                progress?.Report(new("Lyrics.Stage_Decode", 0));
                await decoder.DecodeAsync(source, pcm, ct).ConfigureAwait(false);
                int total = checked((int)(new FileInfo(pcm).Length / 4));
                if (testLimitSeconds > 0) total = Math.Min(total, testLimitSeconds * 16000);
                if (total < 400) throw new InvalidDataException("Lyrics.Error_EmptyAudio");
                int duration = total / 16;
                if (existing != null)
                {
                    existing.DurationMs = duration;
                    await AlignLinesAsync(pcm, existing, options, progress, ct, waitIfPaused).ConfigureAwait(false);
                    return existing;
                }
                await models.EnsureAsync(ModelDirectory(options), options.Performance == LyricsPerformance.HighPrecision, progress, ct).ConfigureAwait(false);
                var document = new LyricsDocument { SourceFile = source, DurationMs = duration };
                var chunks = new List<Chunk>();
                int size = (options.Performance == LyricsPerformance.ResourceSaving ? Math.Min(15, options.ChunkSeconds) : options.ChunkSeconds) * 16000;
                int overlap = options.SongMode ? 32000 : 16000, step = size - overlap;
                await Pause(waitIfPaused, ct);
                progress?.Report(new("Lyrics.Stage_LoadAsr", 0));
                using (var asr = new QwenInference(Path.Combine(ModelDirectory(options), options.Performance == LyricsPerformance.HighPrecision ? "asr-fp32" : "asr"),
                    false, useDml: options.UseGpu, quantized: options.Performance != LyricsPerformance.HighPrecision, threads: Threads(options)))
                {
                    async Task Recognize(int start, int length, int left, int right)
                    {
                        await Pause(waitIfPaused, ct);
                        var audio = PcmAudioDecoder.ReadChunk(pcm, start, length);
                        progress?.Report(new("Lyrics.Stage_Recognize", .05 + .5 * start / total, $"{start / 16000}s / {duration / 1000}s"));
                        if (audio.Length < 400 || audio.All(x => Math.Abs(x) < .00001f)) return;
                        string raw;
                        try { raw = asr.Transcribe(audio, options.Language, ct); }
                        catch (InvalidDataException ex) when (ex.Message == "Lyrics.Error_TokenLimit" && length > 80000)
                        {
                            // A difficult music segment can loop during greedy decoding. Retry smaller
                            // overlapping windows of the same model, retaining explicit ownership ranges.
                            int cut = length / 2 / 160 * 160, pad = Math.Min(16000, cut / 4);
                            document.Warnings.Add("Lyrics.Warning_SplitRetry");
                            await Recognize(start, cut + pad, left, Math.Min(right, start + cut));
                            await Recognize(start + cut - pad, length - cut + pad, Math.Max(left, start + cut), right);
                            return;
                        }
                        var (language, text) = ParseRecognition(raw, options.Language);
                        if (!string.IsNullOrWhiteSpace(text)) chunks.Add(new(start, length, left, right, text, language));
                    }
                    for (int start = 0; start < total; start += step)
                    {
                        int length = Math.Min(size, total - start);
                        // Only skip digital silence; music and sustained vocals must not be cut by speech VAD.
                        int left = start == 0 ? 0 : start + overlap / 2;
                        int right = start + size >= total ? total : start + size - overlap / 2;
                        await Recognize(start, length, left, right);
                        if (start + size >= total) break;
                    }
                    document.Backend = asr.Backend;
                    ReportGpuFallback(document, asr);
                }
                if (chunks.Count == 0) throw new InvalidDataException("Lyrics.Error_NoVocals");
                document.Language = string.Join(", ", chunks.Select(c => c.Language).Distinct());
                progress?.Report(new("Lyrics.Stage_LoadAligner", .55));
                using (var aligner = new QwenInference(Path.Combine(ModelDirectory(options), "aligner"), true, useDml: options.UseGpu, threads: Threads(options)))
                {
                    var words = new List<LyricsWord>();
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        await Pause(waitIfPaused, ct); var chunk = chunks[i];
                        progress?.Report(new("Lyrics.Stage_Align", .55 + .4 * i / chunks.Count, $"{i + 1} / {chunks.Count}"));
                        if (!AlignmentLanguages.Contains(chunk.Language))
                        {
                            // Unsupported alignment languages retain honest chunk timing; never invent word timestamps.
                            document.Lines.Add(new() { Text = chunk.Text, StartMs = chunk.Start / 16, EndMs = (chunk.Start + chunk.Length) / 16 });
                            document.Warnings.Add("Lyrics.Warning_LineTimingOnly"); continue;
                        }
                        var aligned = aligner.Align(PcmAudioDecoder.ReadChunk(pcm, chunk.Start, chunk.Length), chunk.Text, chunk.Language, chunk.Start / 16, ct);
                        words.AddRange(aligned.Where(w => (w.StartMs + w.EndMs) / 2 >= chunk.Left / 16 && (w.StartMs + w.EndMs) / 2 < chunk.Right / 16));
                    }
                    document.Lines.AddRange(GroupWords(words, options.MaxLineCharacters, options.SongMode ? 1500 : 700));
                    if (aligner.Backend != document.Backend) document.Backend = $"ASR: {document.Backend} / Align: {aligner.Backend}";
                    ReportGpuFallback(document, aligner);
                }
                document.Lines = document.Lines.OrderBy(l => l.StartMs).ToList();
                if (document.Lines.Count == 0) throw new InvalidDataException("Lyrics.Error_NoVocals");
                if (document.Lines.SelectMany(l => l.Words).Any(w => w.EndMs <= w.StartMs)) document.Warnings.Add("Lyrics.Warning_Alignment");
                document.Warnings.Add("Lyrics.Warning_Review");
                return document;
            }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            finally { if (File.Exists(pcm)) File.Delete(pcm); }
        }, ct);

    public Task RealignAsync(LyricsDocument document, LyricsOptions options, IProgress<LyricsProgress>? progress, CancellationToken ct)
        => Task.Run(async () =>
        {
            Validate(options);
            string pcm = Path.Combine(Path.GetTempPath(), "fancy-lyrics-" + Guid.NewGuid().ToString("N") + ".pcm");
            try { await decoder.DecodeAsync(document.SourceFile, pcm, ct); document.DurationMs = checked((int)(new FileInfo(pcm).Length / 64)); await AlignLinesAsync(pcm, document, options, progress, ct, null); }
            finally { if (File.Exists(pcm)) File.Delete(pcm); }
        }, ct);
    private async Task AlignLinesAsync(string pcm, LyricsDocument document, LyricsOptions options, IProgress<LyricsProgress>? progress, CancellationToken ct, Func<CancellationToken, Task>? pause)
    {
        if (document.Language == "Auto") document.Language = InferScriptLanguage(string.Concat(document.Lines.Select(l => l.Text)), "Auto");
        if (document.Language == "Auto") document.Language = await DetectLanguageAsync(pcm, document.DurationMs, options, progress, ct, pause).ConfigureAwait(false);
        if (!AlignmentLanguages.Contains(document.Language)) throw new InvalidOperationException("Lyrics.Warning_LineTimingOnly");
        await models.EnsureAlignerAsync(ModelDirectory(options), progress, ct).ConfigureAwait(false);
        using var aligner = new QwenInference(Path.Combine(ModelDirectory(options), "aligner"), true, useDml: options.UseGpu, threads: Threads(options));
        for (int i = 0; i < document.Lines.Count; i++)
        {
            await Pause(pause, ct); var line = document.Lines[i];
            int start = Math.Max(0, line.StartMs - 500), end = Math.Min(document.DurationMs, line.EndMs + 500);
            if (end <= start || end - start > 60000) throw new InvalidDataException("Lyrics.Error_LineDuration");
            progress?.Report(new("Lyrics.Stage_Align", (double)i / document.Lines.Count, $"{i + 1} / {document.Lines.Count}"));
            line.Words = aligner.Align(PcmAudioDecoder.ReadChunk(pcm, start * 16, (end - start) * 16), line.Text, document.Language, start, ct);
            if (line.Words.Count > 0) { line.StartMs = line.Words[0].StartMs; line.EndMs = line.Words[^1].EndMs; }
        }
        document.Backend = aligner.Backend;
        ReportGpuFallback(document, aligner);
    }
    private async Task<string> DetectLanguageAsync(string pcm, int durationMs, LyricsOptions options, IProgress<LyricsProgress>? progress, CancellationToken ct, Func<CancellationToken, Task>? pause)
    {
        await models.EnsureAsync(ModelDirectory(options), options.Performance == LyricsPerformance.HighPrecision, progress, ct).ConfigureAwait(false);
        progress?.Report(new("Lyrics.Stage_LoadAsr", 0));
        using var asr = new QwenInference(Path.Combine(ModelDirectory(options), options.Performance == LyricsPerformance.HighPrecision ? "asr-fp32" : "asr"),
            false, useDml: options.UseGpu, quantized: options.Performance != LyricsPerformance.HighPrecision, threads: Threads(options));
        for (int start = 0; start < Math.Min(durationMs, 120000); start += 28000)
        {
            await Pause(pause, ct);
            progress?.Report(new("Lyrics.Stage_DetectLanguage", 0));
            var audio = PcmAudioDecoder.ReadChunk(pcm, start * 16, Math.Min(30000, durationMs - start) * 16);
            if (audio.Length < 400) break;
            var (language, text) = ParseRecognition(asr.Transcribe(audio, "Auto", ct), "Auto");
            if (!string.IsNullOrWhiteSpace(text) && Languages.Contains(language)) return language;
        }
        throw new InvalidDataException("Lyrics.Error_SelectAlignmentLanguage");
    }
    private static LyricsDocument? FindExisting(string source, string language)
    {
        ATL.Track? track = null;
        try { track = new ATL.Track(source); } catch { /* Some FFmpeg-supported containers have no ATL reader. */ }
        int duration = (int)(track?.DurationMs ?? 0);
        foreach (string extension in new[] { ".krc", ".qrc", ".yrc", ".lrc", ".srt", ".vtt", ".ass" })
        {
            string sidecar = Path.ChangeExtension(source, extension); if (!File.Exists(sidecar)) continue;
            try { var doc = LyricsFileService.Import(sidecar, source, duration); doc.Language = language; return doc; }
            catch (InvalidDataException) { /* An unusable sidecar must not prevent recognition. */ }
        }
        // ATL can represent an untimed tag as one phrase at zero with End=-1.
        // It is not a synchronized track; let ASR create genuine timing in that case.
        var info = track?.Lyrics?.FirstOrDefault(l => l.SynchronizedLyrics.Count > 1 ||
            l.SynchronizedLyrics.Count == 1 && (l.SynchronizedLyrics[0].TimestampStart > 0 || l.SynchronizedLyrics[0].TimestampEnd > 0));
        if (info == null) return null;
        var document = new LyricsDocument { SourceFile = source, DurationMs = duration, Language = language, Model = "Embedded lyrics", Backend = "File" };
        foreach (var phrase in info.SynchronizedLyrics.Where(p => !string.IsNullOrWhiteSpace(p.Text)))
        {
            var line = new LyricsLine { Text = phrase.Text, StartMs = phrase.TimestampStart, EndMs = phrase.TimestampEnd };
            if (phrase.Beats != null) line.Words = phrase.Beats.Select(w => new LyricsWord { Text = w.Text, StartMs = w.TimestampStart, EndMs = w.TimestampEnd }).ToList();
            document.Lines.Add(line);
        }
        for (int i = 0; i < document.Lines.Count; i++)
            if (document.Lines[i].EndMs <= document.Lines[i].StartMs) document.Lines[i].EndMs = i + 1 < document.Lines.Count ? document.Lines[i + 1].StartMs : duration;
        return document.Lines.Count > 0 ? document : null;
    }
    public static (string Language, string Text) ParseRecognition(string raw, string selected)
    {
        int separator = raw.IndexOf("<asr_text>", StringComparison.Ordinal);
        string language = selected;
        if (separator >= 0)
        {
            string prefix = raw[..separator].Trim();
            if (prefix.StartsWith("language ", StringComparison.OrdinalIgnoreCase)) language = prefix[9..].Trim();
            raw = raw[(separator + 10)..];
        }
        if (language == "Auto") language = "English";
        if (raw.Trim() is "<|nospeech|>" or "<no_speech>" or "None") return (language, "");
        string cleaned = Regex.Replace(raw, @"<\|?[^>]+\|?>", "").Trim();
        if (selected == "Auto" && language is "English" or "Auto") language = InferScriptLanguage(cleaned, language);
        return (language, cleaned);
    }
    public static string InferScriptLanguage(string text, string fallback)
    {
        int letters = 0, kana = 0, hangul = 0, han = 0, cyrillic = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (System.Text.Rune.IsLetter(rune)) letters++;
            if (rune.Value is >= 0x3040 and <= 0x30ff) kana++;
            else if (rune.Value is >= 0xac00 and <= 0xd7af or >= 0x1100 and <= 0x11ff) hangul++;
            else if (rune.Value is >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x2ceaf) han++;
            else if (rune.Value is >= 0x0400 and <= 0x052f) cyrillic++;
        }
        if (kana >= 2 && kana + han > letters / 3) return "Japanese";
        if (hangul >= 2 && hangul > letters / 3) return "Korean";
        if (han >= 2 && han > letters / 3) return fallback == "Cantonese" ? fallback : "Chinese";
        if (cyrillic >= 2 && cyrillic > letters / 3) return "Russian";
        return fallback;
    }
    public static List<LyricsLine> GroupWords(List<LyricsWord> words, int maxCharacters, int pauseMs = 700)
    {
        var lines = new List<LyricsLine>(); var current = new List<LyricsWord>(); int chars = 0;
        void Flush()
        {
            if (current.Count == 0) return;
            lines.Add(new() { Text = string.Concat(current.Select(w => w.Text)).Trim(), StartMs = current[0].StartMs,
                EndMs = current[^1].EndMs, Words = current }); current = []; chars = 0;
        }
        foreach (var word in words.OrderBy(w => w.StartMs))
        {
            if (current.Count > 0 && (word.StartMs - current[^1].EndMs > pauseMs || chars + word.Text.Length > maxCharacters || word.EndMs - current[0].StartMs > 10000)) Flush();
            current.Add(word); chars += word.Text.Length;
            if (Regex.IsMatch(word.Text.TrimEnd(), @"[.!?。！？]$")) Flush();
        }
        Flush(); return lines;
    }
    public static string ModelDirectory(LyricsOptions options) => string.IsNullOrWhiteSpace(options.ModelDirectory) ? LyricsModelService.DefaultDirectory : options.ModelDirectory;
    private static int Threads(LyricsOptions options) => options.Performance == LyricsPerformance.ResourceSaving ? Math.Clamp(Environment.ProcessorCount / 4, 1, 4) : 0;
    private static void ReportGpuFallback(LyricsDocument document, QwenInference inference)
    {
        if (inference.GpuFallbackReason != null) document.Warnings.Add("Lyrics.Warning_GpuFallback");
    }
    private static async Task Pause(Func<CancellationToken, Task>? pause, CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (pause != null) await pause(ct).ConfigureAwait(false); }
    public static void Validate(LyricsOptions options)
    {
        if (options.ChunkSeconds is < 10 or > 60 || options.MaxLineCharacters is < 8 or > 120 || !Languages.Contains(options.Language)) throw new ArgumentException("Lyrics.Error_Options");
        if (options.Formats == 0) throw new ArgumentException("Lyrics.Error_Formats");
    }
}
