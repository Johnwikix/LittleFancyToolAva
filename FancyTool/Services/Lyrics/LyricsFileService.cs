using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FancyToolAva.Models;
using Lyricify.Lyrics.Generators;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;

namespace FancyToolAva.Services.Lyrics;

public static partial class LyricsFileService
{
    private static readonly byte[] KrcKey = [0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69];
    public static readonly string[] Extensions = [".lrc", ".elrc", ".krc", ".qrc", ".yrc", ".srt", ".vtt", ".ass", ".txt", ".json"];
    public const LyricsFormats WordFormats = LyricsFormats.Krc | LyricsFormats.Qrc | LyricsFormats.Yrc | LyricsFormats.EnhancedLrc;
    [GeneratedRegex(@"(?m)^\s*((?:\d+:)?\d{2}:\d{2}[,.]\d{3})\s*-->\s*((?:\d+:)?\d{2}:\d{2}[,.]\d{3})[^\r\n]*\r?\n([\s\S]*?)(?=\r?\n\s*\r?\n|\z)")]
    private static partial Regex Subtitles();
    public static LyricsDocument Import(string file, string sourceAudio, int durationMs)
    {
        byte[] bytes = File.ReadAllBytes(file);
        string text;
        if (bytes.AsSpan().StartsWith("krc1"u8))
        {
            byte[] payload = bytes[4..]; for (int i = 0; i < payload.Length; i++) payload[i] ^= KrcKey[i % 16];
            using var input = new MemoryStream(payload); using var zip = new ZLibStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(zip, Encoding.UTF8); text = reader.ReadToEnd().TrimStart('\uFEFF');
        }
        else text = DecodeText(bytes).TrimStart('\uFEFF');
        if (Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            var document = JsonSerializer.Deserialize(text, LyricsJsonContext.Default.LyricsDocument) ?? throw new InvalidDataException("Empty lyrics project.");
            if (document.SchemaVersion != 1 || document.Lines is not { Count: > 0 } || document.Lines.Any(l => l == null || l.Text == null || l.Words == null))
                throw new InvalidDataException("Invalid lyrics project.");
            document.SourceFile = sourceAudio; return document;
        }
        var result = new LyricsDocument { SourceFile = sourceAudio, DurationMs = durationMs, Model = "Existing lyrics", Backend = "File" };
        if (Path.GetExtension(file).ToLowerInvariant() is ".srt" or ".vtt")
        {
            foreach (Match match in Subtitles().Matches(text))
                result.Lines.Add(new LyricsLine { StartMs = ParseTime(match.Groups[1].Value), EndMs = ParseTime(match.Groups[2].Value), Text = match.Groups[3].Value.Trim() });
        }
        else if (Path.GetExtension(file).Equals(".ass", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string row in text.Split('\n'))
            {
                if (!row.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
                var fields = row[9..].Split(',', 10);
                if (fields.Length == 10) result.Lines.Add(new LyricsLine { StartMs = ParseTime(fields[1]), EndMs = ParseTime(fields[2]),
                    Text = Regex.Replace(fields[9], @"\{[^}]*\}", "").Replace("\\N", "\n").Trim() });
            }
        }
        else
        {
            if (Path.GetExtension(file).Equals(".qrc", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(text.Trim(), @"\A[0-9A-Fa-f]+\z"))
                text = Lyricify.Lyrics.Decrypter.Qrc.Decrypter.DecryptLyrics(text.Trim()) ?? throw new InvalidDataException("Invalid QRC payload.");
            if (text.TrimStart().StartsWith('<'))
                text = XDocument.Parse(text).Descendants().Select(x => x.Attribute("LyricContent")?.Value).FirstOrDefault(x => x != null)
                    ?? throw new InvalidDataException("QRC XML has no LyricContent.");
            var type = Path.GetExtension(file).ToLowerInvariant() switch { ".krc" => LyricsRawTypes.Krc, ".qrc" => LyricsRawTypes.Qrc, ".yrc" => LyricsRawTypes.Yrc, _ => LyricsRawTypes.Lrc };
            var data = ParseHelper.ParseLyrics(text, type);
            if (data?.Lines != null)
            {
                foreach (var line in data.Lines.Where(l => l.StartTime != null && !string.IsNullOrWhiteSpace(l.Text)).OrderBy(l => l.StartTime))
                {
                    var item = new LyricsLine { Text = line.Text, StartMs = line.StartTime!.Value, EndMs = line.EndTime ?? line.StartTime.Value };
                    if (line is SyllableLineInfo syllables)
                        foreach (var word in syllables.Syllables) item.Words.Add(new LyricsWord { Text = word.Text, StartMs = word.StartTime, EndMs = word.EndTime });
                    if (item.Words.Count == 0 && item.Text.Contains('<'))
                    {
                        var tags = Regex.Matches(item.Text, @"<((?:\d+:)?\d{2}:\d{2}\.\d{2,3})>([^<]*)");
                        for (int i = 0; i < tags.Count; i++)
                            if (tags[i].Groups[2].Length > 0) item.Words.Add(new() { Text = tags[i].Groups[2].Value, StartMs = ParseTime(tags[i].Groups[1].Value), EndMs = i + 1 < tags.Count ? ParseTime(tags[i + 1].Groups[1].Value) : item.EndMs });
                        if (item.Words.Count > 0) { item.Text = string.Concat(item.Words.Select(w => w.Text)).Trim(); item.EndMs = item.Words[^1].EndMs; }
                    }
                    result.Lines.Add(item);
                }
            }
        }
        if (result.Lines.Count == 0) throw new InvalidDataException("No timed lyrics found; import LRC, KRC, QRC, YRC, SRT, VTT or ASS.");
        for (int i = 0; i < result.Lines.Count; i++)
        {
            var line = result.Lines[i];
            if (line.EndMs <= line.StartMs) line.EndMs = i + 1 < result.Lines.Count ? result.Lines[i + 1].StartMs : durationMs;
            line.EndMs = Math.Max(line.StartMs + 80, line.EndMs);
        }
        return result;
    }
    public static string ToEditor(LyricsDocument document) => LrcGenerator.Generate(ToLibrary(document, 0), LrcGenerator.EndTimeOutputType.None).Replace("\r\n", "\n");
    public static LyricsDocument FromEditor(string content, LyricsDocument original)
    {
        original = JsonSerializer.Deserialize(JsonSerializer.Serialize(original, LyricsJsonContext.Default.LyricsDocument), LyricsJsonContext.Default.LyricsDocument)!;
        var parsed = ParseHelper.ParseLyrics(content, LyricsRawTypes.Lrc);
        if (parsed?.Lines is not { Count: > 0 }) throw new InvalidDataException("Lyrics editor requires [mm:ss.xx] timed lines.");
        var doc = new LyricsDocument { SourceFile = original.SourceFile, DurationMs = original.DurationMs, Language = original.Language,
            Model = original.Model, Backend = original.Backend, Warnings = [.. original.Warnings] };
        foreach (var line in parsed.Lines.OrderBy(l => l.StartTime))
        {
            if (line.StartTime == null || string.IsNullOrWhiteSpace(line.Text)) continue;
            var unchanged = original.Lines.FirstOrDefault(l => l.StartMs == line.StartTime && l.Text.Trim() == line.Text.Trim());
            doc.Lines.Add(unchanged ?? new LyricsLine { Text = line.Text, StartMs = line.StartTime.Value, EndMs = line.EndTime ?? line.StartTime.Value });
        }
        for (int i = 0; i < doc.Lines.Count; i++)
            if (doc.Lines[i].Words.Count == 0 && doc.Lines[i].EndMs <= doc.Lines[i].StartMs) doc.Lines[i].EndMs = i + 1 < doc.Lines.Count ? doc.Lines[i + 1].StartMs : doc.DurationMs;
        if (doc.Lines.Count == 0) throw new InvalidDataException("Lyrics editor has no timed text.");
        return doc;
    }
    public static async Task<string> ExportAsync(LyricsDocument document, string basePath, LyricsFormats formats, int offsetMs, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        if ((formats & WordFormats) != 0 && document.Lines.Any(l => l.Words.Count == 0))
            throw new InvalidOperationException("Lyrics.Export_NeedsAlignment");
        var library = ToLibrary(document, offsetMs);
        var staged = new List<(string Temp, string Final)>();
        try
        {
            foreach (var (format, extension) in new[] { (LyricsFormats.Lrc, ".lrc"), (LyricsFormats.EnhancedLrc, ".elrc"), (LyricsFormats.Krc, ".krc"), (LyricsFormats.Qrc, ".qrc"),
                (LyricsFormats.Srt, ".srt"), (LyricsFormats.Vtt, ".vtt"), (LyricsFormats.Ass, ".ass"), (LyricsFormats.Yrc, ".yrc"), (LyricsFormats.Text, ".txt"), (LyricsFormats.Json, ".json") })
            {
                if ((formats & format) == 0) continue;
                string final = basePath + extension, temp = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
                staged.Add((temp, final)); ct.ThrowIfCancellationRequested();
                if (format == LyricsFormats.Krc) await File.WriteAllBytesAsync(temp, PackKrc(KrcGenerator.Generate(library)), ct);
                else
                {
                    string content = format switch
                    {
                        LyricsFormats.Lrc => LrcGenerator.Generate(library),
                        LyricsFormats.Qrc => new XDocument(new XElement("QrcInfos", new XElement("LyricInfo", new XElement("Lyric_1", new XAttribute("LyricType", "1"), new XAttribute("LyricContent", QrcGenerator.Generate(library)))))).ToString(),
                        LyricsFormats.Yrc => YrcGenerator.Generate(library),
                        LyricsFormats.Text => string.Join(Environment.NewLine, document.Lines.Select(l => l.Text)),
                        LyricsFormats.Json => JsonSerializer.Serialize(Shift(document, offsetMs), LyricsJsonContext.Default.LyricsDocument),
                        LyricsFormats.EnhancedLrc => Enhanced(document, offsetMs),
                        LyricsFormats.Ass => Ass(document, offsetMs),
                        _ => Subtitle(document, offsetMs, format == LyricsFormats.Vtt)
                    };
                    await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct);
                }
            }
            // Always keep an editable result, independently of the selected public export formats.
            string project = basePath + ".fancylyrics.json", projectTemp = project + "." + Guid.NewGuid().ToString("N") + ".tmp";
            staged.Add((projectTemp, project));
            await File.WriteAllTextAsync(projectTemp, JsonSerializer.Serialize(document, LyricsJsonContext.Default.LyricsDocument), ct);
            ct.ThrowIfCancellationRequested();
            foreach (var file in staged) File.Move(file.Temp, file.Final, overwrite: true);
            return project;
        }
        finally { foreach (var file in staged) if (File.Exists(file.Temp)) File.Delete(file.Temp); }
    }
    private static LyricsData ToLibrary(LyricsDocument doc, int offset) => new()
    {
        Lines = doc.Lines.Select(line => line.Words.Count > 0
            ? (ILineInfo)new SyllableLineInfo(line.Words.Select(w => (ISyllableInfo)new SyllableInfo(w.Text, Math.Max(0, w.StartMs + offset), Math.Max(0, w.EndMs + offset))))
            : new LineInfo(line.Text, Math.Max(0, line.StartMs + offset), Math.Max(0, line.EndMs + offset))).ToList()
    };
    public static byte[] PackKrc(string body)
    {
        using var payload = new MemoryStream();
        using (var zip = new ZLibStream(payload, CompressionLevel.Optimal, true)) zip.Write(Encoding.UTF8.GetBytes("\uFEFF" + body));
        byte[] data = payload.ToArray(); for (int i = 0; i < data.Length; i++) data[i] ^= KrcKey[i % 16];
        return [.. "krc1"u8, .. data];
    }
    private static LyricsDocument Shift(LyricsDocument doc, int offset)
    {
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(doc, LyricsJsonContext.Default.LyricsDocument), LyricsJsonContext.Default.LyricsDocument)!;
        foreach (var l in copy.Lines) { l.StartMs = Math.Max(0, l.StartMs + offset); l.EndMs = Math.Max(l.StartMs, l.EndMs + offset);
            foreach (var w in l.Words) { w.StartMs = Math.Max(0, w.StartMs + offset); w.EndMs = Math.Max(w.StartMs, w.EndMs + offset); } }
        return copy;
    }
    private static string Subtitle(LyricsDocument doc, int offset, bool vtt)
    {
        var text = new StringBuilder(vtt ? "WEBVTT\n\n" : ""); int number = 0;
        foreach (var line in doc.Lines)
        {
            if (!vtt) text.AppendLine((++number).ToString(CultureInfo.InvariantCulture));
            text.Append(Time(line.StartMs + offset, vtt ? '.' : ',')).Append(" --> ").AppendLine(Time(Math.Max(line.StartMs + 80, line.EndMs) + offset, vtt ? '.' : ','));
            text.AppendLine(line.Text).AppendLine();
        }
        return text.ToString();
    }
    private static string Enhanced(LyricsDocument doc, int offset) => string.Join("\n", doc.Lines.Select(l =>
        "[" + LrcTime(l.StartMs + offset) + "]" + string.Concat(l.Words.Select(w => "<" + LrcTime(w.StartMs + offset) + ">" + w.Text)) + "<" + LrcTime(l.EndMs + offset) + ">"));
    private static string Ass(LyricsDocument doc, int offset)
    {
        var b = new StringBuilder("[Script Info]\nScriptType: v4.00+\nPlayResX: 1280\nPlayResY: 720\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,Arial,42,&H00FFFFFF,&H0000FFFF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,0,2,30,30,40,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}").Replace("\r", "").Replace("\n", "\\N");
        foreach (var l in doc.Lines)
        {
            b.Append("Dialogue: 0,").Append(AssTime(l.StartMs + offset)).Append(',').Append(AssTime(l.EndMs + offset)).Append(",Default,,0,0,0,,");
            int previous = l.StartMs;
            if (l.Words.Count == 0) b.Append(Escape(l.Text));
            foreach (var w in l.Words)
            {
                int gap = Math.Max(0, (w.StartMs - previous) / 10);
                if (gap > 0) b.Append("{\\k").Append(gap).Append('}');
                b.Append("{\\kf").Append(Math.Max(1, (w.EndMs - w.StartMs) / 10)).Append('}').Append(Escape(w.Text)); previous = w.EndMs;
            }
            b.AppendLine();
        }
        return b.ToString();
    }
    public static int ParseTime(string time)
    {
        var parts = time.Trim().Replace(',', '.').Split(':');
        if (parts.Length is not (2 or 3)) throw new FormatException("Invalid subtitle timestamp.");
        int minutes = int.Parse(parts[^2], CultureInfo.InvariantCulture), hours = parts.Length == 3 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        decimal seconds = decimal.Parse(parts[^1], CultureInfo.InvariantCulture);
        if (hours < 0 || minutes < 0 || seconds is < 0 or >= 60) throw new FormatException("Invalid subtitle timestamp.");
        return checked((int)((hours * 3600L + minutes * 60L + seconds) * 1000));
    }
    private static string DecodeText(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) return Encoding.UTF32.GetString(bytes);
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return new UTF32Encoding(true, true).GetString(bytes);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return Encoding.Unicode.GetString(bytes);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return Encoding.BigEndianUnicode.GetString(bytes);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return CodePagesEncodingProvider.Instance.GetEncoding(54936)!.GetString(bytes); }
    }
    private static string Time(int ms, char separator) { var t = TimeSpan.FromMilliseconds(Math.Max(0, ms)); return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{separator}{t.Milliseconds:000}"; }
    private static string LrcTime(int ms) { var t = TimeSpan.FromMilliseconds(Math.Max(0, ms)); return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}"; }
    private static string AssTime(int ms) { var t = TimeSpan.FromMilliseconds(Math.Max(0, ms)); return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}"; }
}
