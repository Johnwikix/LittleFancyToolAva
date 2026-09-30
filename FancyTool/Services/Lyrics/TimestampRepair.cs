namespace FancyToolAva.Services.Lyrics;

/// <summary>Monotonic subsequence repair, following Qwen's forced-aligner processor (Apache-2.0).</summary>
public static class TimestampRepair
{
    public static int[] Repair(int[] data)
    {
        if (data.Length == 0) return [];
        int n = data.Length; var length = Enumerable.Repeat(1, n).ToArray(); var parent = Enumerable.Repeat(-1, n).ToArray();
        for (int i = 1; i < n; i++)
            for (int j = 0; j < i; j++)
                if (data[j] <= data[i] && length[j] + 1 > length[i]) { length[i] = length[j] + 1; parent[i] = j; }
        var normal = new bool[n]; int index = Array.IndexOf(length, length.Max());
        while (index >= 0) { normal[index] = true; index = parent[index]; }
        int[] result = (int[])data.Clone();
        for (int i = 0; i < n;)
        {
            if (normal[i]) { i++; continue; }
            int end = i; while (end < n && !normal[end]) end++;
            int count = end - i; int? left = i > 0 ? result[i - 1] : null, right = end < n ? result[end] : null;
            for (int k = i; k < end; k++)
                result[k] = left == null ? right!.Value : right == null ? left.Value : count <= 2
                    ? k - (i - 1) <= end - k ? left.Value : right.Value
                    : (int)(left.Value + (right.Value - left.Value) * (double)(k - i + 1) / (count + 1));
            i = end;
        }
        return result;
    }
}
