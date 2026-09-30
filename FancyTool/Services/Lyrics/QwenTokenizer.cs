using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FancyToolAva.Services.Lyrics;

/// <summary>Byte-level BPE with the model's own merge table; no reflection or Python runtime.</summary>
public sealed partial class QwenTokenizer
{
    private readonly Dictionary<string, int> _vocab = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), int> _ranks = new();
    private readonly Dictionary<int, string> _tokens = new();
    private readonly char[] _byteChars = new char[256];
    private readonly Dictionary<char, byte> _charBytes = new();
    [GeneratedRegex(@"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+")]
    private static partial Regex Pieces();
    public QwenTokenizer(string directory)
    {
        int extra = 0;
        for (int b = 0; b < 256; b++)
        {
            bool printable = b is >= 33 and <= 126 or >= 161 and <= 172 or >= 174 and <= 255;
            char c = (char)(printable ? b : 256 + extra++);
            _byteChars[b] = c; _charBytes[c] = (byte)b;
        }
        string tokenizer = Path.Combine(directory, "tokenizer.json");
        using var json = JsonDocument.Parse(File.ReadAllText(File.Exists(tokenizer) ? tokenizer : Path.Combine(directory, "vocab.json")));
        var model = File.Exists(tokenizer) ? json.RootElement.GetProperty("model") : json.RootElement;
        var vocab = File.Exists(tokenizer) ? model.GetProperty("vocab") : model;
        foreach (var p in vocab.EnumerateObject()) { int id = p.Value.GetInt32(); _vocab[p.Name] = id; _tokens[id] = p.Name; }
        int rank = 0;
        if (!File.Exists(tokenizer))
        {
            foreach (string merge in File.ReadLines(Path.Combine(directory, "merges.txt")))
            {
                if (merge.StartsWith('#') || string.IsNullOrWhiteSpace(merge)) continue;
                var pair = merge.Split(' ', 2); _ranks[(pair[0], pair[1])] = rank++;
            }
            return;
        }
        foreach (var added in json.RootElement.GetProperty("added_tokens").EnumerateArray())
            _tokens[added.GetProperty("id").GetInt32()] = added.GetProperty("content").GetString()!;
        foreach (var merge in model.GetProperty("merges").EnumerateArray())
        {
            string[] pair = merge.ValueKind == JsonValueKind.Array ? merge.EnumerateArray().Select(x => x.GetString()!).ToArray() : merge.GetString()!.Split(' ', 2);
            _ranks[(pair[0], pair[1])] = rank++;
        }
    }
    public int[] Encode(string text)
    {
        var result = new List<int>();
        foreach (Match match in Pieces().Matches(text))
        {
            var bytes = Encoding.UTF8.GetBytes(match.Value);
            var pieces = bytes.Select(b => _byteChars[b].ToString()).ToList();
            while (pieces.Count > 1)
            {
                int best = int.MaxValue, index = -1;
                for (int i = 0; i < pieces.Count - 1; i++)
                    if (_ranks.TryGetValue((pieces[i], pieces[i + 1]), out int value) && value < best) { best = value; index = i; }
                if (index < 0) break;
                pieces[index] += pieces[index + 1]; pieces.RemoveAt(index + 1);
            }
            foreach (var piece in pieces) result.Add(_vocab[piece]);
        }
        return result.ToArray();
    }
    public string Decode(IEnumerable<int> ids)
    {
        using var stream = new MemoryStream();
        foreach (int id in ids)
            if (_tokens.TryGetValue(id, out string? token))
                foreach (char c in token) if (_charBytes.TryGetValue(c, out byte b)) stream.WriteByte(b);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
}
