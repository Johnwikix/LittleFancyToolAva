using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    /// <summary>
    /// 模型下载回归：断点续传、重复调用不再重下、以及截断传输保留片段。
    /// 用本地假响应替代网络，条目按真实字节内容生成 SHA-256。
    /// </summary>
    private static int RunModelResumeCases(string workDir)
    {
        int run = 0;
        int failures = 0;

        void Check(bool condition, string message)
        {
            run++;
            if (condition) return;
            failures++;
            Console.WriteLine("  [model] FAIL " + message);
        }

        string directory = Path.Combine(workDir, "resume-models");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);

        byte[] payload = new byte[4 * 1024 * 1024];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31 + (i >> 7));
        string sha = Convert.ToHexString(SHA256.HashData(payload));
        var entry = new ModelManifestEntry("resume_test.onnx",
            "https://example.invalid/resume_test.onnx", "https://example.invalid/resume_test.onnx", sha);
        string finalPath = Path.Combine(directory, entry.FileName);
        string partialPath = finalPath + ".partial";

        // 1) 已有 1 MiB 片段：应从该偏移续传，而不是从 0 重下。
        File.WriteAllBytes(partialPath, payload[..(1024 * 1024)]);
        using (var handler = new RangeProbeHandler(payload))
        using (var http = new HttpClient(handler))
        {
            var service = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
            service.EnsureModelsAsync([entry], directory).GetAwaiter().GetResult();

            Check(handler.Ranges.Count == 1, "续传应只发起一次请求，实际 " + handler.Ranges.Count);
            Check(handler.Ranges.TryPeek(out var range) && range is not null && range.Contains("1048576"),
                "续传请求未带上片段偏移：" + (range ?? "<none>"));
            Check(File.Exists(finalPath) && new FileInfo(finalPath).Length == payload.Length, "续传后文件长度不正确");
            Check(File.ReadAllBytes(finalPath).AsSpan().SequenceEqual(payload), "续传拼接结果与原始内容不一致");
            Check(!File.Exists(partialPath), "完成后未清理 .partial 片段");

            // 2) 已完成的模型再次调用（进入页面 / 重复选择）不得再发请求。
            int requestsBefore = handler.Count;
            service.EnsureModelsAsync([entry], directory).GetAwaiter().GetResult();
            Check(handler.Count == requestsBefore, "已完成的模型被重复下载，新增请求 " + (handler.Count - requestsBefore));

            // 3) 并发调用共用结果：两个任务同时进入，仍不应重复拉取。
            File.Delete(finalPath);
            int concurrentBase = handler.Count;
            var first = service.EnsureModelsAsync([entry], directory);
            var second = service.EnsureModelsAsync([entry], directory);
            Task.WaitAll(first, second);
            Check(handler.Count - concurrentBase <= 1, "并发调用重复下载同一文件");
            Check(File.Exists(finalPath), "并发调用后文件缺失");
        }

        // 4) 服务器忽略 Range（200）：整体重下并覆盖旧片段，结果仍正确。
        File.WriteAllBytes(partialPath, payload[..(512 * 1024)]);
        using (var handler = new RangeProbeHandler(payload) { IgnoreRanges = true })
        using (var http = new HttpClient(handler))
        {
            var service = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
            service.EnsureModelsAsync([entry], directory).GetAwaiter().GetResult();
            Check(File.ReadAllBytes(finalPath).AsSpan().SequenceEqual(payload), "服务器不支持 Range 时结果不正确");
        }

        // 5) 截断传输：保留片段供续传，不当作内容损坏删除。
        File.Delete(finalPath);
        using (var handler = new RangeProbeHandler(payload) { TruncateAfter = 1024 * 1024 })
        using (var http = new HttpClient(handler))
        {
            var service = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
            try
            {
                service.EnsureModelsAsync([entry], directory).GetAwaiter().GetResult();
                Check(false, "截断传输应抛出下载异常");
            }
            catch (ModelDownloadException)
            {
                Check(File.Exists(partialPath), "截断传输丢弃了已下载片段");
                long kept = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
                Check(kept > 0 && kept < payload.Length, "保留的片段长度不合理：" + kept);
            }
        }

        // 6) 片段内容不可信（非前缀垃圾）：丢弃片段后对同一地址从头重下一次，整体仍成功。
        File.Delete(finalPath);
        byte[] garbage = new byte[1024 * 1024];
        for (int i = 0; i < garbage.Length; i++) garbage[i] = 0x5A;
        File.WriteAllBytes(partialPath, garbage);
        using (var handler = new RangeProbeHandler(payload))
        using (var http = new HttpClient(handler))
        {
            var service = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
            try
            {
                service.EnsureModelsAsync([entry], directory).GetAwaiter().GetResult();
                Check(File.Exists(finalPath) && File.ReadAllBytes(finalPath).AsSpan().SequenceEqual(payload),
                    "片段不可信时未能自愈重下");
                Check(handler.Count >= 2, "片段不可信时应重试一次，实际请求 " + handler.Count);
            }
            catch (ModelDownloadException)
            {
                Check(false, "片段不可信时应自愈重下，不应整体失败");
            }
        }

        Console.WriteLine($"----\nmodel resume total={run} failures={failures}");
        return failures == 0 ? 0 : 3;
    }

    /// <summary>声明完整长度但只写出部分字节，模拟连接中断。</summary>
    private sealed class ShortContent(byte[] data, long declaredLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(data, 0, data.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength;
            return true;
        }
    }

    private sealed class RangeProbeHandler(byte[] payload) : HttpMessageHandler
    {
        public ConcurrentQueue<string?> Ranges { get; } = new();
        public bool IgnoreRanges { get; init; }
        public int TruncateAfter { get; init; }
        private int _count;
        public int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            string? range = request.Headers.Range?.ToString();
            Ranges.Enqueue(range);

            int from = 0;
            if (!IgnoreRanges && request.Headers.Range?.Ranges is { Count: 1 } ranges
                && ranges.First().From is { } start)
                from = (int)start;

            byte[] slice = from == 0 ? payload : payload[from..];
            bool truncated = TruncateAfter > 0 && slice.Length > TruncateAfter;
            if (truncated) slice = slice[..TruncateAfter];

            // 截断场景要声明完整长度但只发一部分，模拟真实断流。
            HttpContent content = truncated ? new ShortContent(slice, payload.Length - from) : new ByteArrayContent(slice);
            var response = new HttpResponseMessage(from == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = content
            };
            if (truncated)
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, payload.Length - 1, payload.Length);
            else if (from > 0)
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, payload.Length - 1, payload.Length);
            return Task.FromResult(response);
        }
    }
}
