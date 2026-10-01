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
    // Keep the automatic probe deliberately limited to the nine languages
    // exposed by the first release. The rest of the recognizer's language list
    // remains available when the user selects a language explicitly.
    private static readonly string[] AutoProbeLanguages =
        ["Chinese", "English", "Japanese", "Korean", "French", "Spanish", "Russian", "German", "Portuguese"];
    private static readonly Dictionary<string, string[]> AutoProbeMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = ["the", "and", "you", "that", "this", "with", "for", "are", "have", "love", "i", "me", "my", "we"],
        ["French"] = ["le", "la", "les", "des", "une", "est", "je", "que", "dans", "pour"],
        ["Spanish"] = ["el", "la", "los", "las", "una", "es", "que", "en", "para", "por"],
        ["German"] = ["der", "die", "das", "und", "nicht", "ich", "ein", "ist", "mit", "für"],
        ["Portuguese"] = ["que", "não", "uma", "para", "com", "dos", "das", "por", "está", "não"]
    };
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
                // Auto mode gets a deterministic language hint from embedded metadata
                // or the filename before asking the model. This avoids the common
                // singing failure where a Japanese or Cyrillic song is decoded as English.
                string recognitionLanguage = options.Language == "Auto" ? InferTrackLanguage(source) : options.Language;
                var chunks = new List<Chunk>();
                int size = (options.Performance == LyricsPerformance.ResourceSaving ? Math.Min(15, options.ChunkSeconds) : options.ChunkSeconds) * 16000;
                int overlap = options.SongMode ? 32000 : 16000, step = size - overlap;
                await Pause(waitIfPaused, ct);
                progress?.Report(new("Lyrics.Stage_LoadAsr", 0));
                using (var asr = new QwenInference(Path.Combine(ModelDirectory(options), options.Performance == LyricsPerformance.HighPrecision ? "asr-fp32" : "asr"),
                    false, useDml: options.UseGpu, quantized: options.Performance != LyricsPerformance.HighPrecision, threads: Threads(options)))
                {
                    if (recognitionLanguage == "Auto")
                    {
                        // Qwen's unforced language mode can translate a sung
                        // passage into English. Probe all nine primary languages
                        // with the same ASR session, then lock the best language
                        // before the full pass. The probe uses two short windows
                        // at most, so a quiet intro cannot decide the whole song.
                        progress?.Report(new("Lyrics.Stage_DetectLanguage", .03));
                        recognitionLanguage = ProbeAutoLanguage(asr, pcm, total, ct);
                    }
                    async Task Recognize(int start, int length, int left, int right)
                    {
                        await Pause(waitIfPaused, ct);
                        var audio = PcmAudioDecoder.ReadChunk(pcm, start, length);
                        progress?.Report(new("Lyrics.Stage_Recognize", .05 + .5 * start / total, $"{start / 16000}s / {duration / 1000}s"));
                        if (audio.Length < 400 || audio.All(x => Math.Abs(x) < .00001f)) return;
                        string raw;
                        try { raw = asr.Transcribe(audio, recognitionLanguage, ct); }
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
                        var (language, text) = ParseRecognition(raw, recognitionLanguage);
                        if (recognitionLanguage != "Auto")
                        {
                            string script = InferScriptLanguage(text, "Auto");
                            if (script != "Auto") language = script;
                            else if (LatinLetterCount(text) >= 2)
                                language = InferLatinLanguage(text, recognitionLanguage);
                        }
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
                document.Language = recognitionLanguage != "Auto"
                    ? recognitionLanguage
                    : string.Join(", ", chunks.Select(c => c.Language).Distinct());
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
            if (selected == "Auto" && prefix.StartsWith("language ", StringComparison.OrdinalIgnoreCase)) language = prefix[9..].Trim();
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

    private static string InferTrackLanguage(string source)
    {
        try
        {
            var track = new ATL.Track(source);
            string metadata = string.Join(" ", track.Title, track.Artist, track.Album, Path.GetFileNameWithoutExtension(source));
            return InferScriptLanguage(metadata, "Auto");
        }
        catch { return InferScriptLanguage(Path.GetFileNameWithoutExtension(source), "Auto"); }
    }
    private static string ProbeAutoLanguage(QwenInference asr, string pcm, int total, CancellationToken ct)
    {
        // Score all nine candidates on the same audio windows. The first pass
        // uses one prefill to compare the language tokens; ambiguous tracks then
        // receive a small number of full forced-language decodes. Script and
        // stop-word evidence stabilize CJK/Cyrillic and Latin-script choices.
        int length = Math.Min(total, 8 * 16000);
        if (length < 400) return "Auto";
        var starts = total > length * 2
            ? new[] { 0, Math.Max(0, total - length) }
            : new[] { 0 };
        var scores = AutoProbeLanguages.ToDictionary(language => language, _ => 0d, StringComparer.OrdinalIgnoreCase);
        var usable = AutoProbeLanguages.ToDictionary(language => language, _ => 0, StringComparer.OrdinalIgnoreCase);
        var autoVotes = AutoProbeLanguages.ToDictionary(language => language, _ => 0d, StringComparer.OrdinalIgnoreCase);
        var tokenVotes = AutoProbeLanguages.ToDictionary(language => language, _ => 0d, StringComparer.OrdinalIgnoreCase);
        var trustedScripts = new Dictionary<int, string>();
        foreach (int start in starts.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var audio = PcmAudioDecoder.ReadChunk(pcm, start, length);
            string trustedScript = "Auto";
            try
            {
                var auto = ParseRecognition(asr.Transcribe(audio, "Auto", ct), "Auto");
                if (!string.IsNullOrWhiteSpace(auto.Text))
                {
                    trustedScript = InferScriptLanguage(auto.Text, "Auto");
                    string autoLanguage = trustedScript != "Auto" ? trustedScript : auto.Language;
                    if (AutoProbeLanguages.Contains(autoLanguage, StringComparer.OrdinalIgnoreCase))
                        autoVotes[autoLanguage] += trustedScript != "Auto" && auto.Text.EnumerateRunes().Count(System.Text.Rune.IsLetter) >= 8 ? 2 : 1;
                }
            }
            catch (InvalidDataException ex) when (ex.Message == "Lyrics.Error_TokenLimit") { /* Forced probes can still recover language. */ }
            trustedScripts[start] = trustedScript;
            foreach (var score in asr.GetLanguageScores(audio, AutoProbeLanguages, ct))
                tokenVotes[score.Language] += score.Probability;
        }
        var autoRanked = autoVotes.OrderByDescending(pair => pair.Value).ToArray();
        string autoWinner = autoRanked[0].Key;
        if (autoRanked[0].Value > 0 && (autoRanked[0].Value - autoRanked.ElementAtOrDefault(1).Value >= .5 || autoWinner is "Chinese" or "Japanese" or "Korean" or "Russian"))
            return autoWinner;
        var tokenRanked = AutoProbeLanguages.OrderByDescending(language => tokenVotes[language]).ToArray();
        double tokenTotal = tokenVotes.Values.Sum();
        double tokenWinnerShare = tokenTotal > 0 ? tokenVotes[tokenRanked[0]] / tokenTotal : 0;
        if (autoRanked[0].Value == 0 && tokenWinnerShare >= .8)
            return tokenRanked[0];

        // The cheap prefill scores every language. Only ambiguous cases need
        // full forced decoding, and always include the four non-Latin scripts
        // when English was the unforced guess so Japanese translation errors can
        // still be corrected.
        var deepCandidates = tokenRanked.Take(3).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (autoWinner == "English" || autoRanked[0].Value == 0)
            foreach (string language in new[] { "Chinese", "Japanese", "Korean", "Russian" }) deepCandidates.Add(language);
        foreach (int start in starts.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var audio = PcmAudioDecoder.ReadChunk(pcm, start, length);
            string trustedScript = trustedScripts.GetValueOrDefault(start, "Auto");
            foreach (string language in deepCandidates)
            {
                ct.ThrowIfCancellationRequested();
                QwenInference.ScoredTranscription scored;
                try { scored = asr.TranscribeScored(audio, language, ct); }
                catch (InvalidDataException ex) when (ex.Message == "Lyrics.Error_TokenLimit")
                {
                    // A forced language can occasionally enter a repetition loop
                    // on a dense music passage. Retry a shorter context for the
                    // detector; normal extraction still has its split retry path.
                    int retryLength = Math.Min(audio.Length, 5 * 16000);
                    try { scored = asr.TranscribeScored(audio[..retryLength], language, ct); }
                    catch (InvalidDataException retry) when (retry.Message == "Lyrics.Error_TokenLimit") { continue; }
                }
                string text = ParseRecognition(scored.Text, language).Text;
                if (string.IsNullOrWhiteSpace(text) || !double.IsFinite(scored.AverageLogProbability)) continue;
                scores[language] += scored.AverageLogProbability + ProbeTextEvidence(text, language, trustedScript);
                usable[language]++;
            }
        }
        foreach (string language in AutoProbeLanguages)
            if (usable[language] > 0) scores[language] /= usable[language];
        var ranked = AutoProbeLanguages.OrderByDescending(language => scores[language]).ToArray();
        if (ranked.Length > 1 && usable[ranked[0]] > 0 && scores[ranked[0]] - scores[ranked[1]] >= .35)
            return ranked[0];
        // Instrumental openings can make every forced prompt hallucinate a
        // plausible sentence. Keep a deterministic fallback for that case.
        return ranked.Length > 0 && usable[ranked[0]] > 0 ? ranked[0] : "English";
    }
    private static double ProbeTextEvidence(string text, string language, string trustedScript)
    {
        int letters = text.EnumerateRunes().Count(System.Text.Rune.IsLetter);
        if (letters < 2) return -2;
        string script = InferScriptLanguage(text, "Auto");
        // Forced prompts can make the model emit any requested script. Trust
        // script evidence only when the unforced pass observed the same one.
        double score = script.Equals(trustedScript, StringComparison.OrdinalIgnoreCase) && trustedScript != "Auto" ? 2.5 : 0;
        if (AutoProbeMarkers.TryGetValue(language, out string[]? markers))
        {
            string lower = text.ToLowerInvariant();
            var words = Regex.Matches(lower, @"\p{L}+").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
            score += Math.Min(1.5, markers.Count(words.Contains) * .3);
        }
        string accents = language switch
        {
            "French" => "àâæçéèêëîïôœùûüÿ",
            "Spanish" => "áéíóúüñ¿¡",
            "German" => "äöüß",
            "Portuguese" => "ãõçáâêô",
            _ => ""
        };
        if (accents.Length > 0) score += Math.Min(.8, text.Count(accents.Contains) * .2);
        return score;
    }
    private static string InferLatinLanguage(string text, string fallback)
    {
        var scores = AutoProbeMarkers.Keys.ToDictionary(language => language, _ => 0d, StringComparer.OrdinalIgnoreCase);
        string lower = text.ToLowerInvariant();
        var words = Regex.Matches(lower, @"\p{L}+").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in AutoProbeMarkers)
            scores[pair.Key] += pair.Value.Count(words.Contains);
        foreach (string language in scores.Keys)
        {
            string accents = language switch
            {
                "French" => "àâæçéèêëîïôœùûüÿ",
                "Spanish" => "áéíóúüñ¿¡",
                "German" => "äöüß",
                "Portuguese" => "ãõçáâêô",
                _ => ""
            };
            if (accents.Length > 0) scores[language] += text.Count(accents.Contains) * .8;
        }
        var ranked = scores.OrderByDescending(pair => pair.Value).ToArray();
        if (ranked[0].Value < 2 || ranked[0].Value - ranked[1].Value < 1) return fallback;
        return ranked[0].Key;
    }
    private static int LatinLetterCount(string text)
    {
        int count = 0;
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z') count++;
        return count;
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
        if (inference.GpuFallbackReason != null)
        {
            document.Warnings.Add("Lyrics.Warning_GpuFallback");
            document.Diagnostics.Add($"DirectML fallback: {inference.GpuFallbackReason}");
        }
    }
    private static async Task Pause(Func<CancellationToken, Task>? pause, CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (pause != null) await pause(ct).ConfigureAwait(false); }
    public static void Validate(LyricsOptions options)
    {
        if (options.ChunkSeconds is < 10 or > 60 || options.MaxLineCharacters is < 8 or > 120 || !Languages.Contains(options.Language)) throw new ArgumentException("Lyrics.Error_Options");
        if (options.Formats == 0) throw new ArgumentException("Lyrics.Error_Formats");
    }
}
