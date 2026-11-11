using System.Security.Cryptography;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    /// <summary>
    /// 诊断：验证真实下载源的 Range 行为与拼接完整性。
    /// 取前 <c>prefix</c> 字节 + 从 <c>prefix</c> 开始的范围请求，拼接后与远端完整文件比对 SHA-256。
    /// </summary>
    private static async Task<int> RunHttpRangeProbeAsync(string url, long prefix, string workDir)
    {
        Directory.CreateDirectory(workDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        async Task<byte[]> Fetch(long from, long? to)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Console.WriteLine($"  range {from}-{(to?.ToString() ?? "")} -> {(int)response.StatusCode} " +
                $"content-range={response.Content.Headers.ContentRange} length={response.Content.Headers.ContentLength} " +
                $"final={response.RequestMessage?.RequestUri?.Host}");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("HTTP " + (int)response.StatusCode);
            return await response.Content.ReadAsByteArrayAsync();
        }

        Console.WriteLine($"probe {url} prefix={prefix}");
        byte[] head = await Fetch(0, prefix - 1);
        byte[] tail = await Fetch(prefix, null);
        byte[] joined = new byte[head.Length + tail.Length];
        head.CopyTo(joined, 0);
        tail.CopyTo(joined, head.Length);
        Console.WriteLine($"head={head.Length} tail={tail.Length} joined={joined.Length}");
        Console.WriteLine($"sha(joined)={Convert.ToHexString(SHA256.HashData(joined))}");

        // 与完整下载结果比对，确认"前缀 + 续传"是否等价于整体下载。
        using (var full = new HttpRequestMessage(HttpMethod.Get, url))
        using (var response = await http.SendAsync(full, HttpCompletionOption.ResponseHeadersRead))
        {
            byte[] all = await response.Content.ReadAsByteArrayAsync();
            Console.WriteLine($"sha(full)  ={Convert.ToHexString(SHA256.HashData(all))} len={all.Length}");
            Console.WriteLine(joined.AsSpan().SequenceEqual(all) ? "MATCH: 续传拼接与整体下载一致" : "MISMATCH: 续传拼接内容不同");
            return joined.AsSpan().SequenceEqual(all) ? 0 : 3;
        }
    }
}
