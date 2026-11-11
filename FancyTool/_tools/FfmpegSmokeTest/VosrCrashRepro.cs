using System.Diagnostics;
using Avalonia;
using FancyToolAva;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    /// <summary>
    /// 复现 GUI 图像批处理末尾序列：Task.Run 推理 → ReleaseSessions → 强制压缩 GC。
    /// 每步打印控制台标记并即时 flush；原生崩溃（0xC0000409）无法被托管 catch 接管，
    /// 最后一条标记即崩溃点。异常详情完整打印，弥补 GUI 失败日志缺异常的问题。
    /// </summary>
    private static int RunVosrCrashRepro(string workDir, string? modelsDirectory, string? imagePath, double cancelAfterSeconds = 0)
    {
        modelsDirectory ??= Environment.GetEnvironmentVariable("FANCYTOOL_VOSR_MODELS") ?? "D:/model/FancyTool";
        AppPaths.InitializeModelsDirectory(modelsDirectory);
        string[] missing = ModelManifest.Vosr
            .Select(e => e.FileName)
            .Where(f => !File.Exists(Path.Combine(modelsDirectory, f)))
            .ToArray();
        if (missing.Length > 0)
        {
            Console.WriteLine($"[vosr-crash] SKIP: {missing.Length}/9 graphs missing: {string.Join(", ", missing)}");
            return 0;
        }

        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        using var loggerFactory = new ConsoleLoggerFactory();
        using var sr = new SuperResolutionService(
            loggerFactory.CreateLogger<SuperResolutionService>(),
            new AppPreferences(),
            new ModelDownloadService(new HttpClient(), loggerFactory.CreateLogger<ModelDownloadService>()));
        SpinWait.SpinUntil(() => sr.WarmupTask.IsCompleted, TimeSpan.FromSeconds(30));
        sr.DeviceChanged += d => Mark($"device = {d.Name} (hardware={d.IsHardware}, fallback={d.FallbackReason})");

        SKBitmap source;
        if (imagePath is not null && File.Exists(imagePath))
        {
            source = SKBitmap.Decode(imagePath) ?? throw new InvalidOperationException("decode failed: " + imagePath);
            Mark($"loaded {imagePath} {source.Width}x{source.Height} alpha={source.AlphaType}");
        }
        else
        {
            source = CreateVosrFixture();
            Mark($"using fixture {source.Width}x{source.Height}");
        }

        var clock = Stopwatch.StartNew();
        Exception? failure = null;
        SKBitmap? result = null;
        using var cts = new CancellationTokenSource();
        if (cancelAfterSeconds > 0)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(cancelAfterSeconds));
            Mark($"will CANCEL after {cancelAfterSeconds}s (mid-kernel Terminate)");
        }
        Mark("UPSCALE-START (Task.Run, VOSR 4x)");
        try
        {
            result = Task.Run(() => sr.UpscaleAsync(source, SuperResolutionModel.Vosr1_4B, 4,
                new Progress<double>(p => { if (p >= 0.999 || (int)(p * 100) % 10 == 0) Mark($"progress {p:P0} @{clock.Elapsed.TotalSeconds:F1}s"); }),
                cts.Token)).GetAwaiter().GetResult();
            Mark($"UPSCALE-OK {result.Width}x{result.Height} @{clock.Elapsed.TotalSeconds:F1}s");
            string output = Path.Combine(workDir, "vosr-crash-4x.png");
            using (var image = SKImage.FromBitmap(result))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = File.Create(output))
                data.SaveTo(stream);
            Mark("saved " + output);
        }
        catch (Exception ex)
        {
            failure = ex;
            Mark("UPSCALE-FAILED @" + clock.Elapsed.TotalSeconds.ToString("F1") + "s");
            Console.WriteLine(ex.ToString());
            Console.Out.Flush();
        }
        finally
        {
            result?.Dispose();
            source.Dispose();
        }

        // GUI StartConvert 末尾：先 ReleaseSessions（Task.Run），再强制压缩 GC（Task.Run）。
        Mark("RELEASE-START");
        try { Task.Run(() => sr.ReleaseSessions()).GetAwaiter().GetResult(); Mark("RELEASE-DONE"); }
        catch (Exception ex) { Mark("RELEASE-THREW: " + ex); }

        Mark("GC-START");
        try
        {
            Task.Run(() =>
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }).GetAwaiter().GetResult();
            Mark("GC-DONE");
        }
        catch (Exception ex) { Mark("GC-THREW: " + ex); }

        Mark(failure is null ? "SURVIVED (inference ok)" : "SURVIVED (inference failed, cleanup ok)");
        return failure is null ? 0 : 3;
    }

    private static void Mark(string message)
    {
        Console.WriteLine($"[vosr-crash {DateTime.Now:HH:mm:ss.fff}] {message}");
        Console.Out.Flush();
    }

    /// <summary>
    /// 主动制造 GPU 显存压力：两条独立 VOSR 链并发推理（各约 3.3 GB 权重），
    /// 期望触发 OpenVINO CL_OUT_OF_RESOURCES。修复后该失败不得再以原生崩溃收尾：
    /// 会话被毒化跳过释放，随后 ReleaseSessions/Dispose 不得崩溃。
    /// </summary>
    private static int RunVosrOomCase(string workDir, string? modelsDirectory, string? imagePath, int chains)
    {
        modelsDirectory ??= Environment.GetEnvironmentVariable("FANCYTOOL_VOSR_MODELS") ?? "D:/model/FancyTool";
        AppPaths.InitializeModelsDirectory(modelsDirectory);
        string[] missing = ModelManifest.Vosr
            .Select(e => e.FileName)
            .Where(f => !File.Exists(Path.Combine(modelsDirectory, f)))
            .ToArray();
        if (missing.Length > 0)
        {
            Console.WriteLine($"[vosr-oom] SKIP: {missing.Length}/9 graphs missing");
            return 0;
        }

        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        chains = Math.Clamp(chains <= 0 ? 2 : chains, 2, 3);
        using var loggerFactory = new ConsoleLoggerFactory();
        var services = new List<SuperResolutionService>();
        for (int i = 0; i < chains; i++)
            services.Add(new SuperResolutionService(
                loggerFactory.CreateLogger<SuperResolutionService>(),
                new AppPreferences(),
                new ModelDownloadService(new HttpClient(), loggerFactory.CreateLogger<ModelDownloadService>())));
        try
        {
            foreach (var sr in services) SpinWait.SpinUntil(() => sr.WarmupTask.IsCompleted, TimeSpan.FromSeconds(30));
            Mark($"oom: {chains} chains ready, launching concurrent 2x inference");

            var results = services.Select((sr, i) => Task.Run(() =>
            {
                SKBitmap source = imagePath is not null && File.Exists(imagePath)
                    ? SKBitmap.Decode(imagePath)!
                    : CreateVosrFixture();
                try
                {
                    var result = sr.UpscaleAsync(source, SuperResolutionModel.Vosr1_4B, 2).GetAwaiter().GetResult();
                    Mark($"oom: chain{i} OK {result.Width}x{result.Height}");
                    result.Dispose();
                    return (i, (Exception?)null);
                }
                catch (Exception ex)
                {
                    Mark($"oom: chain{i} FAILED {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                    return (i, (Exception?)ex);
                }
                finally { source.Dispose(); }
            })).ToArray();
            Task.WaitAll(results, TimeSpan.FromMinutes(8));
            int failed = results.Count(t => t.IsCompletedSuccessfully && t.Result.Item2 is not null);
            Mark($"oom: inference finished, failedChains={failed}");

            // 关键：释放被 OOM 破坏的会话组。修复前这里会 0xC0000409 原生崩溃。
            foreach (var sr in services)
            {
                Mark("oom: releasing chain sessions");
                sr.ReleaseSessions();
                Mark("oom: chain released");
            }
            Mark("oom: SURVIVED (no native abort on contaminated session release)");
            return 0;
        }
        finally
        {
            foreach (var sr in services)
            {
                try { sr.Dispose(); } catch { }
            }
        }
    }
    /// Restore 全程持有 _runGate，ReleaseSessions 必须等待推理结束后再释放会话。
    /// 修复前该场景会在 OpenVINO/DML 会话内部触发原生崩溃或 OrtException。
    /// </summary>
    private static int RunVosrRaceCase(string workDir, string? modelsDirectory)
    {
        modelsDirectory ??= Environment.GetEnvironmentVariable("FANCYTOOL_VOSR_MODELS") ?? "D:/model/FancyTool";
        AppPaths.InitializeModelsDirectory(modelsDirectory);
        string[] missing = ModelManifest.Vosr
            .Select(e => e.FileName)
            .Where(f => !File.Exists(Path.Combine(modelsDirectory, f)))
            .ToArray();
        if (missing.Length > 0)
        {
            Console.WriteLine($"[vosr-race] SKIP: {missing.Length}/9 graphs missing");
            return 0;
        }

        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        using var loggerFactory = new ConsoleLoggerFactory();
        using var sr = new SuperResolutionService(
            loggerFactory.CreateLogger<SuperResolutionService>(),
            new AppPreferences(),
            new ModelDownloadService(new HttpClient(), loggerFactory.CreateLogger<ModelDownloadService>()));
        SpinWait.SpinUntil(() => sr.WarmupTask.IsCompleted, TimeSpan.FromSeconds(30));
        using var source = CreateVosrFixture();
        int scale = 2; // 15 块，推理约十几秒，便于在中途插入释放。

        var upscale = Task.Run(() => sr.UpscaleAsync(source, SuperResolutionModel.Vosr1_4B, scale));
        Mark("race: upscale started, waiting 8s for inference to be in flight");
        Thread.Sleep(8000);
        var release = Task.Run(() =>
        {
            Mark("race: ReleaseSessions called during inference");
            sr.ReleaseSessions();
            Mark("race: ReleaseSessions returned");
        });

        bool releasedEarly = release.Wait(TimeSpan.FromMilliseconds(500));
        // 释放若在推理结束前返回即为竞态（会话在推理中被拆除）。
        Mark(releasedEarly ? "race: RELEASE RETURNED WHILE TILE INFERENCE LIKELY RUNNING" : "race: release correctly waited for inference");

        SKBitmap? result = null;
        Exception? failure = null;
        try { result = upscale.Wait(TimeSpan.FromSeconds(300)) ? upscale.Result : throw new TimeoutException("upscale did not finish"); }
        catch (Exception ex) { failure = ex; }

        release.Wait(TimeSpan.FromSeconds(60));
        int exit = 0;
        if (failure is not null)
        {
            Mark("race: FAILED inference did not complete: " + failure);
            exit = 3;
        }
        else
        {
            Mark($"race: OK {result!.Width}x{result.Height}, no overlap crash");
            result.Dispose();
        }
        Mark("race: SURVIVED");
        return exit;
    }

    private sealed class ConsoleLoggerFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class ConsoleLogger(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                Console.WriteLine($"[{logLevel}] {category.Split('.').Last()}: {formatter(state, exception)}");
                if (exception is not null) Console.WriteLine(exception.ToString());
                Console.Out.Flush();
            }
        }
    }
}