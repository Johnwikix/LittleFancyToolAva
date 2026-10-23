using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunImageGpuBenchmark(string workDir, string[] args)
    {
        string? originalProfile = Environment.GetEnvironmentVariable("FANCYTOOL_VIDEO_PROFILE");
        try
        {
            string Arg(string key, string fallback) => Array.IndexOf(args, key) is var index && index >= 0 ? args[index + 1] : fallback;
            int width = int.Parse(Arg("--width", "640")), height = int.Parse(Arg("--height", "360"));
            int scale = int.Parse(Arg("--scale", "2")), repeats = int.Parse(Arg("--repeats", "3"));
            bool alpha = args.Contains("--alpha");
            var preferences = new AppPreferences
            {
                UseSuperResolutionNpu = false,
                SuperResolutionTileSizeIndex = int.Parse(Arg("--tile-index", "2"))
            };
            using var http = new HttpClient(new ImageGpuOfflineDownloads());
            using var service = new SuperResolutionService(NullLogger<SuperResolutionService>.Instance,
                preferences, new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance));
            service.WarmupTask.GetAwaiter().GetResult();
            if (service.LastDownloadError != null) throw new InvalidOperationException(service.LastDownloadError);
            using var source = CreateImageGpuFixture(width, height, alpha);
            var records = new List<object>();
            var selected = args.Contains("--image-model")
                ? new[] { Enum.Parse<SuperResolutionModel>(Arg("--image-model", "SpanClearRealityV1")) }
                : Enum.GetValues<SuperResolutionModel>();
            foreach (var model in selected)
            {
                var times = new List<double>();
                var allocations = new List<long>();
                var hashes = new HashSet<string>();
                int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
                double firstMs = 0;
                for (int iteration = 0; iteration <= repeats; iteration++)
                {
                    if (args.Contains("--trace"))
                        Environment.SetEnvironmentVariable("FANCYTOOL_VIDEO_PROFILE", Path.GetFullPath(Path.Combine(workDir, $"{model}-{iteration}")));
                    using var metrics = args.Contains("--trace") ? VideoPipelineMetrics.Begin() : null;
                    long allocated = GC.GetTotalAllocatedBytes(precise: true);
                    var start = Stopwatch.GetTimestamp();
                    using var output = service.UpscaleAsync(source, model, scale).GetAwaiter().GetResult();
                    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    long bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                    if (service.InferenceDevice is not { IsHardware: true } device || !device.Name.StartsWith("GPU"))
                        throw new InvalidOperationException("图片推理未使用 GPU：" + service.InferenceDevice);
                    if (output.Width != width * scale || output.Height != height * scale)
                        throw new InvalidOperationException("图片输出尺寸不正确。");
                    string hash = Convert.ToHexString(SHA256.HashData(output.GetPixelSpan()));
                    if (iteration == 0) firstMs = elapsed;
                    else { times.Add(elapsed); allocations.Add(bytes); hashes.Add(hash); }
                    if (iteration == repeats)
                    {
                        string name = model + (alpha ? "-alpha" : "-opaque");
                        File.WriteAllBytes(Path.Combine(workDir, name + ".bgra"), output.GetPixelSpan().ToArray());
                        using var image = SKImage.FromBitmap(output);
                        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                        using var stream = File.Create(Path.Combine(workDir, name + ".png"));
                        png.SaveTo(stream);
                    }
                    Console.WriteLine($"IMAGE {model} alpha={alpha} iteration={iteration}: {elapsed:F3}ms, {bytes} bytes, {service.InferenceDevice}");
                }
                times.Sort();
                var record = new { model = model.ToString(), alpha, width, height, scale, preferences.SuperResolutionTileSizeIndex,
                    repeats, firstMs, warmMedianMs = times[times.Count / 2], timesMs = times,
                    allocatedBytes = allocations, allocatedBytesMean = allocations.Average(),
                    gen0 = GC.CollectionCount(0) - collections[0], gen1 = GC.CollectionCount(1) - collections[1],
                    gen2 = GC.CollectionCount(2) - collections[2], warmOutputHashes = hashes, device = service.InferenceDevice };
                records.Add(record);
                File.WriteAllText(Path.Combine(workDir, "measurements.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
                if (hashes.Count != 1) throw new InvalidOperationException("同一图片的重复推理输出不稳定。");
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                try
                {
                    using var unexpected = service.UpscaleAsync(source, model, scale, ct: canceled.Token).GetAwaiter().GetResult();
                    throw new InvalidOperationException("图片推理忽略取消。");
                }
                catch (OperationCanceledException) { }
                using var retry = service.UpscaleAsync(source, model, scale).GetAwaiter().GetResult();
                if (!hashes.Contains(Convert.ToHexString(SHA256.HashData(retry.GetPixelSpan()))))
                    throw new InvalidOperationException("取消后重试结果变化。");
                if (args.Contains("--cancel-in-flight"))
                {
                    using var inFlight = new CancellationTokenSource();
                    int reports = 0;
                    try
                    {
                        using var unexpected = service.UpscaleAsync(source, model, scale,
                            new ImageGpuInlineProgress(value =>
                            {
                                if (value > .05 && Interlocked.Increment(ref reports) == 1) inFlight.CancelAfter(1);
                            }), inFlight.Token).GetAwaiter().GetResult();
                        throw new InvalidOperationException("分块推理未响应在途取消。");
                    }
                    catch (OperationCanceledException) when (inFlight.IsCancellationRequested) { }
                    using var recovered = service.UpscaleAsync(source, model, scale).GetAwaiter().GetResult();
                    if (!hashes.Contains(Convert.ToHexString(SHA256.HashData(recovered.GetPixelSpan()))))
                        throw new InvalidOperationException("在途取消后缓冲区复用改变输出。");
                    Console.WriteLine("Image in-flight cancellation and pooled-buffer retry OK: " + model);
                }
                service.ReleaseSessions();
            }
            Console.WriteLine("Image GPU OK: hardware execution, repeated output, cancellation and retry checked.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("Image GPU FAIL: " + error);
            return 1;
        }
        finally { Environment.SetEnvironmentVariable("FANCYTOOL_VIDEO_PROFILE", originalProfile); }
    }

    private static SKBitmap CreateImageGpuFixture(int width, int height, bool alpha)
    {
        var image = new SKBitmap(width, height, SKColorType.Bgra8888, alpha ? SKAlphaType.Premul : SKAlphaType.Opaque);
        var pixels = image.GetPixelSpan();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int index = y * image.RowBytes + x * 4;
            int opacity = alpha ? (x * 11 + y * 7) % 256 : 255;
            pixels[index] = (byte)((x * 13 + y * 5) % 256 * opacity / 255);
            pixels[index + 1] = (byte)((x * 3 + y * 17) % 256 * opacity / 255);
            pixels[index + 2] = (byte)((x * 19 + y * 23) % 256 * opacity / 255);
            pixels[index + 3] = (byte)opacity;
        }
        return image;
    }

    private sealed class ImageGpuOfflineDownloads : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("GPU 复测要求预先安装并校验模型，不允许临时下载：" + request.RequestUri);
    }

    private sealed class ImageGpuInlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
