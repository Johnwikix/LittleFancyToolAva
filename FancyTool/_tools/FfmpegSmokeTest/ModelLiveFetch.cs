using FancyToolAva.Services;
using Microsoft.Extensions.Logging;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    /// <summary>
    /// 诊断：用真实网络走 <see cref="ModelDownloadService"/> 下载单个模型条目，
    /// 校验清单 SHA-256 是否与落盘内容一致（用于定位"总是 SHA 不符"的下载问题）。
    /// </summary>
    private static int RunModelLiveFetch(string url, string fileName, string sha256, string directory)
    {
        Directory.CreateDirectory(directory);
        var entry = new ModelManifestEntry(fileName, url, url, sha256);
        using var http = new HttpClient();
        var service = new ModelDownloadService(http, new ConsoleLogger<ModelDownloadService>());
        try
        {
            service.EnsureModelsAsync([entry], directory).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {fileName}: {ex.Message}");
            string bad = Path.Combine(directory, fileName + ".partial");
            if (File.Exists(bad)) Console.WriteLine($"partial kept: {new FileInfo(bad).Length} bytes");
            return 3;
        }

        string path = Path.Combine(directory, fileName);
        var info = new FileInfo(path);
        string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        Console.WriteLine($"OK {fileName} {info.Length} bytes sha={actual}");
        Console.WriteLine(actual.Equals(sha256, StringComparison.OrdinalIgnoreCase) ? "SHA MATCH" : "SHA MISMATCH");
        return actual.Equals(sha256, StringComparison.OrdinalIgnoreCase) ? 0 : 3;
    }
}

internal sealed class ConsoleLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        Console.WriteLine($"  [{logLevel}] {formatter(state, exception)}");
        if (exception is not null) Console.WriteLine("    " + exception.GetType().Name + ": " + exception.Message);
    }
}
