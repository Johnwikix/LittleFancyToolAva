using System.Diagnostics;
using System.Net;
using Avalonia;
using FancyToolAva;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    /// <summary>
    /// VOSR-1.4B 链路烟测。走真实 <see cref="SuperResolutionService"/> 与 Windows ML 运行时：
    /// 按目标分辨率先双三次放大、铺 512 窗口、九图串联（DINO → VAE 编码 → DiT 分段 → 单步更新 → VAE 解码），
    /// 只保留窗口内部 384 区域。九张图不在本地时整体跳过，不触发 3.3 GB 下载。
    /// </summary>
    private static int RunVosrCase(string workDir, string? modelsDirectory)
    {
        int run = 0;
        int failures = 0;

        void Check(bool condition, string message)
        {
            run++;
            if (condition) return;
            failures++;
            Console.WriteLine("  [vosr] FAIL " + message);
        }

        modelsDirectory ??= Environment.GetEnvironmentVariable("FANCYTOOL_VOSR_MODELS")
            ?? Path.Combine(workDir, "vosr-models");
        AppPaths.InitializeModelsDirectory(modelsDirectory);

        string[] missing = ModelManifest.Vosr
            .Select(entry => entry.FileName)
            .Where(file => !File.Exists(Path.Combine(modelsDirectory, file)))
            .ToArray();
        if (missing.Length > 0)
        {
            Console.WriteLine($"[vosr] SKIP ({missing.Length}/9 graphs missing in {modelsDirectory})");
            _skips++;
            return 0;
        }

        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        using var http = new HttpClient(new NotFoundHandler());
        using var sr = new SuperResolutionService(NullLogger<SuperResolutionService>.Instance,
            new AppPreferences(), new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance));

        // 启动预热会去下载 ESRGAN 家族模型；离线应答让这段流程快速失败，不影响 VOSR 判定。
        SpinWait.SpinUntil(() => sr.WarmupTask.IsCompleted, TimeSpan.FromSeconds(30));

        VideoSrDevice? device = null;
        sr.DeviceChanged += d => device = d;

        using var source = CreateVosrFixture();
        var stopwatch = Stopwatch.StartNew();
        using var result = sr.UpscaleAsync(source, SuperResolutionModel.Vosr1_4B, 4).GetAwaiter().GetResult();
        stopwatch.Stop();

        string output = Path.Combine(workDir, "vosr-4x.png");
        using (var image = SKImage.FromBitmap(result))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(output))
            data.SaveTo(stream);

        Check(result.Width == source.Width * 4 && result.Height == source.Height * 4,
            $"输出尺寸 {result.Width}x{result.Height} 与 4x 不符");
        Check(HasDetail(result), "输出为纯色或全黑，推理链路异常");
        Check(sr.IsModelAvailable(SuperResolutionModel.Vosr1_4B), "整链应被识别为已安装");
        // DeviceChanged 由服务的 SynchronizationContext 派发；无消息循环时需要手动抽消息。
        try { PumpUntil(() => device is not null); }
        catch (TimeoutException) { }
        Check(device is not null, "未上报推理设备");
        Console.WriteLine($"[vosr] {source.Width}x{source.Height} -> {result.Width}x{result.Height} " +
            $"in {stopwatch.Elapsed.TotalSeconds:F1}s on {device?.Name ?? "unknown"} -> {output}");

        Console.WriteLine($"----\nvosr total={run} failures={failures}");
        return failures == 0 ? 0 : 3;
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>构造带文字与细线的低分素材，使生成式恢复有明确的可测目标。</summary>
    private static SKBitmap CreateVosrFixture()
    {
        var bitmap = new SKBitmap(new SKImageInfo(480, 320, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        using (var gradient = new SKPaint())
        using (var line = new SKPaint { Color = SKColors.Black, StrokeWidth = 1, IsAntialias = false })
        using (var face = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default)
        {
            gradient.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(480, 320),
                [new SKColor(24, 32, 48), new SKColor(232, 236, 240)], SKShaderTileMode.Clamp);
            canvas.DrawRect(new SKRect(0, 0, 480, 320), gradient);
            for (int i = 0; i < 24; i++)
                canvas.DrawLine(0, 12 + i * 12, 480, 12 + i * 12, line);
            using var text = new SKPaint { Color = SKColors.White, IsAntialias = false, TextSize = 18, Typeface = face };
            canvas.DrawText("VOSR 1.4B smoke", 24, 60, text);
            canvas.DrawText("sharp edge / 细节恢复", 24, 96, text);
        }
        return bitmap;
    }

    /// <summary>以相邻像素差异判断输出是否仍有结构，避免把纯色图当成成功。</summary>
    private static bool HasDetail(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan();
        int stride = bitmap.Width * 4;
        long variance = 0;
        for (int y = 0; y < bitmap.Height; y += 7)
        {
            for (int x = 4; x < bitmap.Width; x += 7)
            {
                int p = y * stride + x * 4;
                variance += Math.Abs(pixels[p] - pixels[p - 4]);
            }
        }
        return variance > 1000;
    }
}
