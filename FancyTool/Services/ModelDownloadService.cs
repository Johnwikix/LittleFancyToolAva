using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message) : base(message) { }
    public ModelDownloadException(string message, Exception inner) : base(message, inner) { }
}

public sealed class ModelDownloadService
{
    private readonly HttpClient _http;
    private readonly ILogger<ModelDownloadService> _logger;
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromMinutes(15);

    public ModelDownloadService(HttpClient http, ILogger<ModelDownloadService> logger)
    {
        _http = http;
        _logger = logger;
        _http.Timeout = PerFileTimeout + TimeSpan.FromSeconds(30);
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
        Directory.CreateDirectory(targetDirectory);

        var failures = new List<string>();
        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = entries[i];
            string finalPath = Path.Combine(targetDirectory, entry.FileName);
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

        var urls = ResolveUrls(entry);

        Exception? lastError = null;
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DownloadAndVerifyAsync(url, finalPath, entry, progress, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Download attempt failed for {Url}", url);
                lastError = ex;
                TryDeletePartial(finalPath);
            }
        }

        throw new ModelDownloadException(
            $"All download endpoints failed for {entry.FileName}.", lastError!);
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
        progress?.Report(new ModelDownloadProgress(
            entry.FileName, ModelDownloadStage.Connecting, 0, null));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FancyTool", "1.1"));

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ModelDownloadException(
                $"HTTP {(int)response.StatusCode} from {url}");
        }

        long? total = response.Content.Headers.ContentLength;

        string tempPath = finalPath + ".partial";
        try
        {
            await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                long downloaded = 0;
                int read;
                var lastReport = 0L;
                progress?.Report(new ModelDownloadProgress(
                    entry.FileName, ModelDownloadStage.Downloading, 0, total));
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    downloaded += read;
                    // Throttle progress reports: ~50 Hz cap is wasteful for 33 MB
                    // on slow links, so emit at most once per 256 KB.
                    if (downloaded - lastReport >= 256 * 1024)
                    {
                        lastReport = downloaded;
                        progress?.Report(new ModelDownloadProgress(
                            entry.FileName, ModelDownloadStage.Downloading, downloaded, total));
                    }
                }
                await dst.FlushAsync(ct).ConfigureAwait(false);
            }

            progress?.Report(new ModelDownloadProgress(
                entry.FileName, ModelDownloadStage.Verifying, new FileInfo(tempPath).Length, total));

            if (!MatchesSha256(tempPath, entry.Sha256))
            {
                throw new ModelDownloadException(
                    $"SHA256 mismatch for {entry.FileName} (downloaded from {url}).");
            }

            // Atomic-ish replace: on Windows, File.Move(overwrite) is atomic.
            File.Move(tempPath, finalPath, overwrite: true);
            progress?.Report(new ModelDownloadProgress(
                entry.FileName, ModelDownloadStage.Done, new FileInfo(finalPath).Length, total));
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static bool MatchesSha256(string path, string expectedUpperHex)
    {
        if (string.IsNullOrEmpty(expectedUpperHex)) return true;
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(stream);
            var actual = Convert.ToHexString(bytes);
            return string.Equals(actual, expectedUpperHex, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
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