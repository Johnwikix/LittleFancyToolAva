using System.Diagnostics;
using System.Runtime.InteropServices;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FancyToolAva.Services.Lyrics;
using Microsoft.Extensions.Logging.Abstractions;

string mode = args.ElementAtOrDefault(0) ?? "formats";
string work = Path.GetFullPath(args.ElementAtOrDefault(1) ?? ".qa/lyrics/results"); Directory.CreateDirectory(work);
if (mode == "metadata")
{
    var track = new ATL.Track(args[2]); Console.WriteLine("duration=" + track.DurationMs);
    foreach (var info in track.Lyrics)
        foreach (var phrase in info.SynchronizedLyrics) Console.WriteLine($"{phrase.TimestampStart} {phrase.TimestampEnd} chars={phrase.Text.Length} beats={phrase.Beats?.Count}");
    return;
}
if (mode == "network")
{
    var networkComparison = new LyricsNetworkComparisonService();
    var comparisons = await networkComparison.FetchAsync(args[2], default);
    Directory.CreateDirectory(work);
    foreach (string diagnostic in networkComparison.Diagnostics) Console.WriteLine("NETWORK_DIAGNOSTIC " + diagnostic);
    foreach (var comparison in comparisons)
    {
        string safe = string.Concat(comparison.Provider.Where(char.IsLetterOrDigit));
        string basePath = Path.Combine(work, Path.GetFileNameWithoutExtension(args[2]) + "." + safe);
        string project = await LyricsFileService.ExportAsync(comparison.Document, basePath, LyricsFormats.Lrc | LyricsFormats.EnhancedLrc | LyricsFormats.Krc | LyricsFormats.Qrc | LyricsFormats.Yrc | LyricsFormats.Json, 0, default);
        Console.WriteLine($"NETWORK {comparison.Provider} match={comparison.Match} title={comparison.Title} lines={comparison.Document.Lines.Count} words={comparison.Document.Lines.Sum(l => l.Words.Count)} {project}");
    }
    if (comparisons.Count == 0) Console.WriteLine("NETWORK no usable public lyric source");
    return;
}
if (mode is "raw" or "rawlang" or "language-scores")
{
    string pcm = Path.Combine(work, "raw.pcm"); await new PcmAudioDecoder().DecodeAsync(args[3], pcm, default);
    using var model = new QwenInference(args[2], false);
    int seconds = int.Parse(args.ElementAtOrDefault(6) ?? "30");
    if (mode == "language-scores")
    {
        var languages = new[] { "Chinese", "English", "Japanese", "Korean", "French", "Spanish", "Russian", "German", "Portuguese" };
        foreach (int start in args.Skip(4).Select(int.Parse).DefaultIfEmpty(0))
        {
            Console.WriteLine("WINDOW " + start);
            float[] sample = PcmAudioDecoder.ReadChunk(pcm, start * 16000, 12 * 16000);
            foreach (var score in model.GetLanguageScores(sample, languages, default).OrderByDescending(s => s.Probability))
                Console.WriteLine($"TOKEN {score.Language} {score.Probability:F6}");
            foreach (string language in languages)
            {
                try
                {
                    var result = model.TranscribeScored(sample, language, default);
                    Console.WriteLine($"TEXT {language} lp={result.AverageLogProbability:F4} tokens={result.TokenCount} {LyricsExtractionService.ParseRecognition(result.Text, language).Text}");
                }
                catch (InvalidDataException ex) when (ex.Message == "Lyrics.Error_TokenLimit") { Console.WriteLine($"TEXT {language} TOKEN_LIMIT"); }
            }
        }
        Console.WriteLine("BACKEND=" + model.Backend + " FALLBACK=" + model.GpuFallbackReason);
        File.Delete(pcm); return;
    }
    string raw = model.Transcribe(PcmAudioDecoder.ReadChunk(pcm, int.Parse(args.ElementAtOrDefault(4) ?? "0") * 16000, seconds * 16000), mode == "rawlang" ? args.ElementAtOrDefault(5) ?? "Auto" : "Auto", default);
    Console.WriteLine("RAW=" + raw); Console.WriteLine("PARSED=" + LyricsExtractionService.ParseRecognition(raw, "Auto")); File.Delete(pcm); return;
}
if (mode is "backend" or "gpu-cancel")
{
    string pcm = Path.Combine(work, "backend.pcm");
    try
    {
        await new PcmAudioDecoder().DecodeAsync(args[3], pcm, default);
        using var model = new QwenInference(args[2], false, dmlDeviceId: int.Parse(args.ElementAtOrDefault(4) ?? "0"));
        Console.WriteLine("BACKEND=" + model.Backend + " FALLBACK=" + model.GpuFallbackReason);
        if (args.ElementAtOrDefault(4) == "-1" && (model.Backend != "CPU" || model.GpuFallbackReason == null)) throw new Exception("Unavailable GPU did not fall back");
        float[] audio = PcmAudioDecoder.ReadChunk(pcm, 0, 30 * 16000);
        if (mode == "gpu-cancel")
        {
            if (model.Backend != "DirectML") throw new Exception("GPU cancellation test requires DirectML");
            using var cts = new CancellationTokenSource(); cts.CancelAfter(500);
            try { model.Transcribe(audio, "English", cts.Token); throw new Exception("GPU cancellation ignored"); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                if (model.Backend != "DirectML" || model.GpuFallbackReason != null) throw new Exception("Cancellation incorrectly triggered fallback");
                Console.WriteLine("PASS GPU cancellation without CPU retry");
            }
            return;
        }
        string raw = model.Transcribe(audio, "English", default);
        if (string.IsNullOrWhiteSpace(LyricsExtractionService.ParseRecognition(raw, "English").Text)) throw new Exception("Backend produced no usable text");
        Console.WriteLine("PASS backend inference " + raw);
    }
    finally { if (File.Exists(pcm)) File.Delete(pcm); }
    return;
}
if (mode == "validate")
{
    foreach (string project in Directory.GetFiles(args[2], "*.fancylyrics.json", SearchOption.AllDirectories))
    {
        var doc = LyricsFileService.Import(project, "validation.wav", 0); string stem = project[..^".fancylyrics.json".Length];
        foreach (string ext in new[] { ".lrc", ".elrc", ".krc", ".qrc", ".yrc", ".srt", ".vtt", ".ass", ".json" })
        {
            var other = LyricsFileService.Import(stem + ext, doc.SourceFile, doc.DurationMs);
            if (other.Lines.Count != doc.Lines.Count || !other.Lines.Select(l => l.Text.Trim()).SequenceEqual(doc.Lines.Select(l => l.Text.Trim()))) throw new Exception("Generated roundtrip failed: " + ext + " " + project);
            if (ext is ".elrc" or ".krc" or ".qrc" or ".yrc" && other.Lines.Sum(l => l.Words.Count) != doc.Lines.Sum(l => l.Words.Count)) throw new Exception("Generated words lost: " + ext);
        }
        Console.WriteLine("PASS generated exports " + Path.GetFileName(project));
    }
    return;
}
if (mode == "frontend")
{
    var tokenizer = new QwenTokenizer(args[2]);
    foreach (string text in new[] { "system\n", "language English", "Hello, I'm singing!", "中文歌词と日本語 한국어", "français español русский Deutsch português", "  123\n🎵" })
        Console.WriteLine(text.Replace("\n", "\\n") + "\t" + string.Join(',', tokenizer.Encode(text)));
    float[] audio = Enumerable.Range(0, 32000).Select(i => (float)(.2 * Math.Sin(i * Math.PI * 2 * 440 / 16000) + .1 * Math.Sin(i * Math.PI * 2 * 1337 / 16000))).ToArray();
    File.WriteAllBytes(Path.Combine(work, "audio.f32"), MemoryMarshal.AsBytes(audio.AsSpan()).ToArray());
    float[] mel = new LogMelExtractor().Extract(audio, out int frames, default);
    File.WriteAllBytes(Path.Combine(work, "mel.f32"), MemoryMarshal.AsBytes(mel.AsSpan()).ToArray()); Console.WriteLine("frames=" + frames); return;
}
if (mode == "formats")
{
    var doc = new LyricsDocument { SourceFile = "fixture.wav", DurationMs = 5000, Language = "English",
        Lines = [new() { Text = "Hello 世界", StartMs = 1120, EndMs = 2640, Words = [new() { Text = "Hello ", StartMs = 1120, EndMs = 1760 }, new() { Text = "世界", StartMs = 2000, EndMs = 2640 }] },
            new() { Text = "日本語 한국어", StartMs = 3000, EndMs = 4500, Words = [new() { Text = "日本語 한국어", StartMs = 3000, EndMs = 4500 }] }] };
    string result = await LyricsFileService.ExportAsync(doc, Path.Combine(work, "roundtrip"), (LyricsFormats)1023, 200, default);
    foreach (string ext in new[] { ".lrc", ".elrc", ".krc", ".qrc", ".yrc", ".srt", ".vtt", ".ass", ".json" })
    {
        var imported = LyricsFileService.Import(Path.Combine(work, "roundtrip" + ext), "fixture.wav", 5000);
        if (imported.Lines.Count != 2 || imported.Lines[0].StartMs != 1320 || imported.Lines[0].Text.Trim() != "Hello 世界") throw new Exception("Roundtrip failed: " + ext);
        if (ext is ".krc" or ".qrc" or ".yrc" && imported.Lines[0].Words.Count != 2) throw new Exception("Word timings lost: " + ext);
        Console.WriteLine("PASS " + ext);
    }
    string decrypted = Lyricify.Lyrics.Decrypter.Krc.Decrypter.DecryptLyrics(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(work, "roundtrip.krc"))))!;
    if (!decrypted.Contains("Hello")) throw new Exception("KRC consumer rejected export");
    if (!LyricsFileService.FromEditor(LyricsFileService.ToEditor(doc), doc).Lines.All(l => l.Words.Count > 0)) throw new Exception("Editor lost word timings");
    string wordEditor = LyricsFileService.ToEditor(doc, LyricsFormats.Krc);
    if (!wordEditor.Contains('<')) throw new Exception("Word-format preview did not expose word timestamps");
    if (!LyricsFileService.FromEditor(wordEditor, doc).Lines.All(l => l.Words.Count > 0)) throw new Exception("Word-format editor lost timings");
    var edited = LyricsFileService.FromEditor(LyricsFileService.ToEditor(doc), doc); edited.Lines[0].Words[0].StartMs = 99;
    if (doc.Lines[0].Words[0].StartMs != 1120) throw new Exception("Editor changed the original document");
    var zero = new LyricsDocument { DurationMs = 5000, Lines = [new() { Text = "長音", StartMs = 1200, EndMs = 1200, Words = [new() { Text = "長音", StartMs = 1200, EndMs = 1200 }] }] };
    if (LyricsFileService.FromEditor(LyricsFileService.ToEditor(zero), zero).Lines[0].EndMs != 1200) throw new Exception("Editor fabricated word duration");
    File.WriteAllText(Path.Combine(work, "short.vtt"), "WEBVTT\n\n00:01.320 --> 00:02.840\nHello 世界\n\n");
    if (LyricsFileService.Import(Path.Combine(work, "short.vtt"), "fixture.wav", 5000).Lines[0].EndMs != 2840) throw new Exception("Short VTT timestamps failed");
    File.WriteAllBytes(Path.Combine(work, "gbk.lrc"), System.Text.CodePagesEncodingProvider.Instance.GetEncoding(54936)!.GetBytes("[00:01.320]中文歌词\n"));
    if (LyricsFileService.Import(Path.Combine(work, "gbk.lrc"), "fixture.wav", 5000).Lines[0].Text != "中文歌词") throw new Exception("GBK decoding failed");
    if (QwenInference.AlignmentUnits("孤独な振りをしてるの", "Japanese").Count >= 10) throw new Exception("Japanese segmentation failed");
    if (LyricsExtractionService.ParseRecognition("language English<asr_text>日本語の歌詞です", "Auto").Language != "Japanese") throw new Exception("Auto script correction failed");
    var repaired = TimestampRepair.Repair([100, 200, 999, 300, 400]);
    if (!repaired.SequenceEqual(repaired.Order())) throw new Exception("Timestamp repair failed");
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { await LyricsFileService.ExportAsync(doc, Path.Combine(work, "cancelled"), LyricsFormats.Lrc, 0, cts.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { if (Directory.GetFiles(work, "cancelled*").Length != 0) throw new Exception("Cancellation left files"); }
    Console.WriteLine("PASS KRC decoder, editor isolation, short VTT, GBK, Japanese segmentation, timestamps, cancelled export (JSON reflection disabled)"); return;
}
if (mode == "decode")
{
    foreach (string file in Directory.GetFiles(args[2]).Where(f => LyricsExtractionViewExtensions.IsAudio(f)))
    {
        var timer = Stopwatch.StartNew(); string pcm = Path.Combine(work, Path.GetFileName(file) + ".pcm");
        try { await new PcmAudioDecoder().DecodeAsync(file, pcm, default); Console.WriteLine($"DECODE {Path.GetFileName(file)} {new FileInfo(pcm).Length / 64000.0:F2}s {timer.Elapsed.TotalSeconds:F2}s"); }
        catch (Exception ex) { Console.WriteLine($"DECODE_FAILED {Path.GetFileName(file)} {ex.Message}"); }
        finally { if (File.Exists(pcm)) File.Delete(pcm); }
    }
    return;
}
using var http = new HttpClient();
var models = new LyricsModelService(new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance));
var service = new LyricsExtractionService(models, new PcmAudioDecoder());
if (mode == "language-regression")
{
    string expected = args.ElementAtOrDefault(4) ?? "Japanese";
    int limit = int.Parse(args.ElementAtOrDefault(5) ?? "30");
    var regressionOptions = new LyricsOptions
    {
        ModelDirectory = Path.GetFullPath(args[2]), OutputDirectory = work,
        Language = "Auto", PreferExisting = false, Formats = LyricsFormats.Lrc,
        UseGpu = true
    };
    var regressionDocument = await service.ExtractAsync(args[3], regressionOptions, null, default, testLimitSeconds: limit);
    Console.WriteLine($"LANGUAGE_RESULT {regressionDocument.Language} backend={regressionDocument.Backend}");
    if (!string.Equals(regressionDocument.Language, expected, StringComparison.OrdinalIgnoreCase))
        throw new Exception($"Expected auto language {expected}, got {regressionDocument.Language}");
    return;
}
var options = new LyricsOptions { ModelDirectory = Path.GetFullPath(args[2]), OutputDirectory = work, Language = args.ElementAtOrDefault(4) ?? "Auto", PreferExisting = false, Formats = (LyricsFormats)1023 };
var watch = Stopwatch.StartNew();
var progress = new Progress<LyricsProgress>(p => Console.WriteLine($"{watch.Elapsed.TotalSeconds:F1}s {p.Stage} {p.Fraction:P0} {p.Detail}"));
if (mode == "realign")
{
    using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[3]));
    var original = LyricsFileService.Import(args[3], json.RootElement.GetProperty("SourceFile").GetString()!, 0);
    await service.RealignAsync(original, options, progress, default);
    await LyricsFileService.ExportAsync(original, Path.Combine(work, "realigned"), options.Formats, 0, default);
    Console.WriteLine($"PASS realignment {original.Lines.Sum(l => l.Words.Count)} words {watch.Elapsed.TotalSeconds:F2}s"); return;
}
if (args.Length > 6) options.Performance = Enum.Parse<LyricsPerformance>(args[6]);
if (args.Length > 7) options.UseGpu = bool.Parse(args[7]);
var document = await service.ExtractAsync(args[3], options, progress, default, testLimitSeconds: int.Parse(args.ElementAtOrDefault(5) ?? "30"));
string output = await LyricsFileService.ExportAsync(document, Path.Combine(work, Path.GetFileNameWithoutExtension(args[3])), options.Formats, 0, default);
Console.WriteLine($"RESULT {document.Language} {document.Lines.Count} lines {document.Lines.Sum(l => l.Words.Count)} words {watch.Elapsed.TotalSeconds:F2}s peak={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576}MiB backend={document.Backend} {output}");
foreach (var l in document.Lines) Console.WriteLine($"[{l.StartMs},{l.EndMs}] {l.Text}");

internal static class LyricsExtractionViewExtensions
{
    public static bool IsAudio(string path) => new[] { ".mp3", ".wav", ".flac", ".m4a", ".dsf", ".dff", ".wv", ".opus", ".ogg" }.Contains(Path.GetExtension(path).ToLowerInvariant());
}
