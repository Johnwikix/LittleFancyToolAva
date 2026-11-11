using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

/// <summary>片段或整包的字节与清单不符：可丢弃后从头重下一次。</summary>
public sealed class ModelContentMismatchException : Exception
{
    public ModelContentMismatchException(string message) : base(message) { }
}

public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message) : base(message) { }
    public ModelDownloadException(string message, Exception inner) : base(message, inner) { }
}

public sealed class ModelDownloadService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http;
    private readonly ILogger<ModelDownloadService> _logger;

    // 大文件（单图最高 908 MB）不能用整体超时：慢链路下 15 分钟会稳定截断并导致每次重来。
    // 改为按"无数据可读"的空闲超时判定，并通过 Range 续传保留已下载部分。
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);

    // 已校验通过的 (长度, 最后写入时间)：避免每次进入页面重复哈希 3.3 GB 的模型。
    private static readonly ConcurrentDictionary<string, (long Length, DateTime WriteTimeUtc)> VerifiedFiles =
        new(StringComparer.OrdinalIgnoreCase);

    public ModelDownloadService(HttpClient http, ILogger<ModelDownloadService> logger)
    {
        _http = http;
        _logger = logger;
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Ensures every entry is present on disk under <paramref name="targetDirectory"/>.
    /// Already-valid files (matching SHA256) are skipped. Missing or invalid files
    /// are downloaded from the mirror first, falling back to the official source.
    /// </summary>
    public async Task EnsureModelsAsync(
        IReadOnlyList<ModelManifestEntry> entries,
        string targetDirectory,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        using var access = await ModelStorage.AcquireAsync(ct).ConfigureAwait(false);
        await EnsureModelsCoreAsync(entries, targetDirectory, progress, ct).ConfigureAwait(false);
    }

    // Resolve the managed root after acquiring access, and retain it through native
    // session creation so validation and inference always open the same files.
    internal async Task<T> UseModelsAsync<T>(IReadOnlyList<ModelManifestEntry> entries,
        Func<string> directory, Func<string, T> use, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        using var access = await ModelStorage.AcquireAsync(ct).ConfigureAwait(false);
        string root = directory();
        await EnsureModelsCoreAsync(entries, root, progress, ct).ConfigureAwait(false);
        return use(root);
    }

    private async Task EnsureModelsCoreAsync(IReadOnlyList<ModelManifestEntry> entries,
        string targetDirectory, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDirectory);

        var failures = new List<string>();
        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = entries[i];
            string finalPath = Path.Combine(targetDirectory, entry.FileName);
            var gate = FileGates.GetOrAdd(Path.GetFullPath(finalPath), static _ => new SemaphoreSlim(1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EnsureOneAsync(entry, finalPath, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure model {File}", entry.FileName);
                failures.Add($"{entry.FileName}: {ex.Message}");
                progress?.Report(new ModelDownloadProgress(
                    entry.FileName, ModelDownloadStage.Failed, 0, null, ex.Message));
            }
            finally { gate.Release(); }
        }

        if (failures.Count > 0)
            throw new ModelDownloadException(
                "Failed to download models: " + string.Join("; ", failures));
    }

    private async Task EnsureOneAsync(
        ModelManifestEntry entry,
        string finalPath,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        if (File.Exists(finalPath) && MatchesSha256(finalPath, entry.Sha256))
        {
            progress?.Report(new ModelDownloadProgress(
                entry.FileName, ModelDownloadStage.Done, new FileInfo(finalPath).Length, null));
            return;
        }

        if (entry.BundledRelativePath is { } relativePath)
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, relativePath);
            if (!MatchesSha256(bundled, entry.Sha256))
                throw new ModelDownloadException($"Bundled model missing or corrupt: {entry.FileName}");
            string temporary = finalPath + ".partial";
            try
            {
                progress?.Report(new ModelDownloadProgress(entry.FileName, ModelDownloadStage.Verifying, 0, null));
                using (var input = File.OpenRead(bundled))
                using (var output = File.Create(temporary))
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                if (!MatchesSha256(temporary, entry.Sha256)) throw new ModelDownloadException($"Model copy verification failed: {entry.FileName}");
                ct.ThrowIfCancellationRequested();
                File.Move(temporary, finalPath, overwrite: true);
                progress?.Report(new ModelDownloadProgress(entry.FileName, ModelDownloadStage.Done, new FileInfo(finalPath).Length, null));
                return;
            }
            finally { TryDelete(temporary); }
        }

        var urls = ResolveUrls(entry);

        Exception? lastError = null;
        foreach (var url in urls)
        {
            // 内容不一致只会来自"复用了不可信的片段"（旧版本残留或不同 revision）：
            // 清空后对同一地址从头再下一次，避免整个条目直接失败。
            for (int attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await DownloadAndVerifyAsync(url, finalPath, entry, progress, ct).ConfigureAwait(false);
                    return;
                }
                catch (ModelContentMismatchException ex)
                {
                    _logger.LogWarning(ex, "Content mismatch for {File} from {Url}; discarding fragment and retrying once",
                        entry.FileName, url);
                    TryDeletePartial(finalPath);
                    lastError = ex;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 网络错误保留 .partial，下一次（含下次启动）通过 Range 续传。
                    _logger.LogWarning(ex, "Download attempt failed for {Url}", url);
                    lastError = ex;
                    break;
                }
            }
        }

        throw new ModelDownloadException(
            $"All download endpoints failed for {entry.FileName}. {lastError?.Message}", lastError!);
    }

    private static IReadOnlyList<string> ResolveUrls(ModelManifestEntry entry)
    {
        // If the user (or launcher) overrides the Hugging Face endpoint via the
        // standard HF_ENDPOINT variable, swap the host on the official URL.
        string? overrideHost = Environment.GetEnvironmentVariable("HF_ENDPOINT");
        var urls = new List<string>(3) { entry.MirrorUrl, entry.SourceUrl };
        if (!string.IsNullOrWhiteSpace(overrideHost))
        {
            string normalized = overrideHost.TrimEnd('/');
            if (entry.SourceUrl.StartsWith("https://huggingface.co/", StringComparison.OrdinalIgnoreCase))
            {
                string path = entry.SourceUrl["https://huggingface.co".Length..];
                urls.Insert(0, normalized + path);
            }
        }
        return urls;
    }

    private async Task DownloadAndVerifyAsync(
        string url,
        string finalPath,
        ModelManifestEntry entry,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        string tempPath = finalPath + ".partial";

        // 独占打开片段后再取长度：GUI 与 CLI 同时运行时不会用过期偏移续传（否则重复追加导致内容错乱）。
        FileStream destination;
        try
        {
            destination = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);
        }
        catch (IOException ex)
        {
            throw new ModelDownloadException(
                $"{entry.FileName}: another process is already downloading this file.", ex);
        }

        await using (destination)
        {
            long resumeFrom = destination.Length;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FancyTool", "1.1"));
            if (resumeFrom > 0) request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            progress?.Report(new ModelDownloadProgress(
                entry.FileName, ModelDownloadStage.Connecting, resumeFrom, null));

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token)
                .ConfigureAwait(false);
            idle.CancelAfter(IdleTimeout);

            long start;
            long? total;
            if (response.StatusCode == System.Net.HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange is { From: { } from } range)
            {
                start = from;
                total = range.Length;
                if (start != resumeFrom)
                {
                    _logger.LogWarning("Resume offset mismatch for {File}: local={Local}, server={Server}",
                        entry.FileName, resumeFrom, start);
                    start = 0;
                }
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                throw new ModelContentMismatchException(
                    $"Server rejected resume range for {entry.FileName}; cached fragment is unusable.");
            }
            else if (response.IsSuccessStatusCode)
            {
                // 服务器忽略 Range（或本地没有片段）：整体重下。
                start = 0;
                total = response.Content.Headers.ContentLength;
            }
            else
            {
                throw new ModelDownloadException($"HTTP {(int)response.StatusCode} from {url}");
            }

            try
            {
                // 从头写时截断旧片段；续传时定位到声明偏移。
                destination.SetLength(start);
                destination.Position = start;

                await using (var src = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false))
                {
                    var buffer = new byte[64 * 1024];
                    long downloaded = start;
                    long lastReport = downloaded;
                    progress?.Report(new ModelDownloadProgress(
                        entry.FileName, ModelDownloadStage.Downloading, downloaded, total));
                    int read;
                    while ((read = await src.ReadAsync(buffer, idle.Token).ConfigureAwait(false)) > 0)
                    {
                        // 每次读到数据都刷新空闲计时，慢链路只要仍有进展就不会被截断。
                        idle.CancelAfter(IdleTimeout);
                        await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        downloaded += read;
                        if (downloaded - lastReport >= 256 * 1024)
                        {
                            lastReport = downloaded;
                            progress?.Report(new ModelDownloadProgress(
                                entry.FileName, ModelDownloadStage.Downloading, downloaded, total));
                        }
                    }
                    await destination.FlushAsync(ct).ConfigureAwait(false);
                }

                long length = destination.Length;
                progress?.Report(new ModelDownloadProgress(
                    entry.FileName, ModelDownloadStage.Verifying, length, total));

                // 连接提前断开（未收到完整长度）不是内容错误：保留片段，下次续传。
                if (total is { } expected && length < expected)
                    throw new ModelDownloadException(
                        $"{entry.FileName}: incomplete transfer ({length}/{expected} bytes), partial kept for resume.");
                if (total is { } oversized && length > oversized)
                {
                    TryDelete(tempPath);
                    throw new ModelContentMismatchException(
                        $"{entry.FileName}: received {length} bytes but expected {oversized}.");
                }

                await destination.FlushAsync(ct).ConfigureAwait(false);
                destination.Close();

                if (!MatchesSha256(tempPath, entry.Sha256))
                {
                    // 诊断：保留坏包用于比对（FANCYTOOL_KEEP_BAD_MODEL=1）。
                    if (Environment.GetEnvironmentVariable("FANCYTOOL_KEEP_BAD_MODEL") == "1")
                        try { File.Move(tempPath, tempPath + ".bad", overwrite: true); } catch { }
                    TryDelete(tempPath);
                    throw new ModelContentMismatchException(
                        $"SHA256 mismatch for {entry.FileName} (downloaded from {url}).");
                }

                // Atomic-ish replace: on Windows, File.Move(overwrite) is atomic.
                File.Move(tempPath, finalPath, overwrite: true);
                VerifiedFiles.TryRemove(tempPath, out _);
                progress?.Report(new ModelDownloadProgress(
                    entry.FileName, ModelDownloadStage.Done, new FileInfo(finalPath).Length, total));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                throw new ModelDownloadException(
                    $"{entry.FileName}: no data for {IdleTimeout.TotalSeconds:0}s, partial download kept for resume.", ex);
            }
        }
    }

    /// <summary>
    /// Verifies the pinned digest. Large files are opened with share flags and retried: antivirus or
    /// the search indexer briefly holding a freshly written 460 MB graph must not be mistaken for
    /// content corruption (that used to trigger a full re-download of an already valid file).
    /// </summary>
    private bool MatchesSha256(string path, string expectedUpperHex)
    {
        if (string.IsNullOrEmpty(expectedUpperHex)) return true;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                if (VerifiedFiles.TryGetValue(path, out var verified)
                    && verified.Length == info.Length && verified.WriteTimeUtc == info.LastWriteTimeUtc)
                    return true;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                using var sha = SHA256.Create();
                string actual = Convert.ToHexString(sha.ComputeHash(stream));
                bool matches = string.Equals(actual, expectedUpperHex, StringComparison.OrdinalIgnoreCase);
                if (matches) VerifiedFiles[path] = (info.Length, info.LastWriteTimeUtc);
                else _logger.LogWarning("SHA256 mismatch for {Path}: actual={Actual} expected={Expected} bytes={Bytes}",
                    path, actual, expectedUpperHex, info.Length);
                return matches;
            }
            catch (IOException ex) when (attempt < 3)
            {
                _logger.LogDebug(ex, "SHA256 check retry {Attempt} for {Path}", attempt + 1, path);
                Thread.Sleep(200 * (attempt + 1));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SHA256 check failed for {Path}", path);
                return false;
            }
        }
    }

    private static void TryDeletePartial(string finalPath) => TryDelete(finalPath + ".partial");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
