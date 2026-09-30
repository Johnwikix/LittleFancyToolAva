using System.Text.Json;
namespace FancyToolAva.Services.Lyrics;
// TinySegmenter 0.2, copyright 2008 Taku Kudo. BSD-3-Clause; see External/NOTICE-Lyrics.txt.
internal static class JapaneseSegmenter
{
    private static readonly Dictionary<string, Dictionary<string, int>> Weights = Load();
    private static Dictionary<string, Dictionary<string, int>> Load()
    {
        using var stream = typeof(JapaneseSegmenter).Assembly.GetManifestResourceStream("FancyToolAva.Resources.LyricsModels.tinysegmenter.json")!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateObject().ToDictionary(v => v.Name, v => v.Value.GetInt32()));
    }
    private static int Weight(string table, string key) => Weights[table].GetValueOrDefault(key);
    private static string Type(string text)
    {
        if (text.Length != 1) return "O"; char c = text[0];
        if ("一二三四五六七八九十百千万億兆".Contains(c)) return "M";
        if (c is >= '一' and <= '龠' || "々〆ヵヶ".Contains(c)) return "H";
        if (c is >= 'ぁ' and <= 'ん') return "I";
        if (c is >= 'ァ' and <= 'ヴ' or >= 'ｱ' and <= 'ﾝ' || "ーﾞｰ".Contains(c)) return "K";
        if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= 'ａ' and <= 'ｚ' or >= 'Ａ' and <= 'Ｚ') return "A";
        if (c is >= '0' and <= '9' or >= '０' and <= '９') return "N";
        return "O";
    }
    public static List<string> Segment(string input)
    {
        if (string.IsNullOrEmpty(input)) return [];
        var seg = new List<string> { "B3", "B2", "B1" }; seg.AddRange(input.EnumerateRunes().Select(r => r.ToString())); seg.AddRange(["E1", "E2", "E3"]);
        var ctype = seg.Select(Type).ToArray(); var result = new List<string>(); string word = seg[3], p1 = "U", p2 = "U", p3 = "U";
        for (int i = 4; i < seg.Count - 3; i++)
        {
            int score = -332;
            string w1 = seg[i-3], w2 = seg[i-2], w3 = seg[i-1], w4 = seg[i], w5 = seg[i+1], w6 = seg[i+2];
            string c1 = ctype[i-3], c2 = ctype[i-2], c3 = ctype[i-1], c4 = ctype[i], c5 = ctype[i+1], c6 = ctype[i+2];
            score += Weight("UP1", p1);
            score += Weight("UP2", p2);
            score += Weight("UP3", p3);
            score += Weight("BP1", p1 + p2);
            score += Weight("BP2", p2 + p3);
            score += Weight("UW1", w1);
            score += Weight("UW2", w2);
            score += Weight("UW3", w3);
            score += Weight("UW4", w4);
            score += Weight("UW5", w5);
            score += Weight("UW6", w6);
            score += Weight("BW1", w2 + w3);
            score += Weight("BW2", w3 + w4);
            score += Weight("BW3", w4 + w5);
            score += Weight("TW1", w1 + w2 + w3);
            score += Weight("TW2", w2 + w3 + w4);
            score += Weight("TW3", w3 + w4 + w5);
            score += Weight("TW4", w4 + w5 + w6);
            score += Weight("UC1", c1);
            score += Weight("UC2", c2);
            score += Weight("UC3", c3);
            score += Weight("UC4", c4);
            score += Weight("UC5", c5);
            score += Weight("UC6", c6);
            score += Weight("BC1", c2 + c3);
            score += Weight("BC2", c3 + c4);
            score += Weight("BC3", c4 + c5);
            score += Weight("TC1", c1 + c2 + c3);
            score += Weight("TC2", c2 + c3 + c4);
            score += Weight("TC3", c3 + c4 + c5);
            score += Weight("TC4", c4 + c5 + c6);
            score += Weight("UQ1", p1 + c1);
            score += Weight("UQ2", p2 + c2);
            score += Weight("UQ3", p3 + c3);
            score += Weight("BQ1", p2 + c2 + c3);
            score += Weight("BQ2", p2 + c3 + c4);
            score += Weight("BQ3", p3 + c2 + c3);
            score += Weight("BQ4", p3 + c3 + c4);
            score += Weight("TQ1", p2 + c1 + c2 + c3);
            score += Weight("TQ2", p2 + c2 + c3 + c4);
            score += Weight("TQ3", p3 + c1 + c2 + c3);
            score += Weight("TQ4", p3 + c2 + c3 + c4);
            string p = "O";
            if (score > 0) { result.Add(word); word = ""; p = "B"; }
            p1 = p2; p2 = p3; p3 = p; word += seg[i];
        }
        result.Add(word); return result;
    }
}
