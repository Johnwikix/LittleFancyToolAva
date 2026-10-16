using System.Security.Cryptography;
using Avalonia;
using System.Text.Json;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using FFmpeg.AutoGen;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunSuperResolutionWithDispatcher(string source, string workDir, string[] args)
    {
        Avalonia.AppBuilder.Configure<Avalonia.Application>().UsePlatformDetect().SetupWithoutStarting();
        var task = Task.Run(() => RunSuperResolutionCases(source, workDir, args));
        using var finished = new CancellationTokenSource();
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { if (task.IsCompleted) finished.Cancel(); };
        timer.Start();
        Avalonia.Threading.Dispatcher.UIThread.MainLoop(finished.Token);
        timer.Stop();
        return task.GetAwaiter().GetResult();
    }

    private static int RunSuperResolutionCases(string source, string workDir, string[] args)
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        using var http = new HttpClient();
        var downloader = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
        using var sr = new VideoSuperResolutionService(downloader, NullLogger<VideoSuperResolutionService>.Instance,
            new AppPreferences { SuperResolutionTileSizeIndex = 2 });
        var service = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance, sr);
        var records = new List<object>();
        int referenceArgument = Array.IndexOf(args, "--sr-reference-root");
        string? references = referenceArgument >= 0 ? Path.Combine(Path.GetFullPath(args[referenceArgument + 1]), "references") : null;
        int modelArgument = Array.IndexOf(args, "--sr-model");
        int backendArgument = Array.IndexOf(args, "--sr-backend");
        VideoSrBackend selectedBackend = backendArgument >= 0 ? Enum.Parse<VideoSrBackend>(args[backendArgument + 1]) : VideoSrBackend.Auto;
        bool? selectedHalf = args.Contains("--sr-fp16") ? true : args.Contains("--sr-fp32") ? false : null;
        sr.DeviceChanged += device => Console.WriteLine($"DEVICE {device.Name}; fallback={device.FallbackReason}");

        foreach (var model in VideoSrModelCatalog.Models)
        {
            if (modelArgument >= 0 && model.Model != Enum.Parse<VideoSrModel>(args[modelArgument + 1])) continue;
            sr.EnsureModelAsync(model.Model).GetAwaiter().GetResult();
            foreach (bool half in model.SupportsFp16 ? new[] { false, true } : new[] { false })
            foreach (var backend in new[] { selectedBackend })
            {
                if (selectedHalf.HasValue && half != selectedHalf.Value) continue;
                string name = $"{model.Model}-{(half ? "fp16" : "fp32")}-{backend}";
                RunExtra("packaged GPU model, tiled frame and timed video: " + name, () =>
                {
                    var graph = model.GetGraph(half);
                    string path = Path.Combine(AppPaths.ModelsDirectory, "VideoSr", graph.FileName);
                    if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(graph.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Installed model hash mismatch");
                    var actualBackend = model.RequiresDirectMl ? VideoSrBackend.DirectMl : model.RequiresVendorGpu || half ? VideoSrBackend.VendorGpu : backend;
                    var numeric = new List<object>();
                    if (references != null)
                    {
                        using var tensor = new OrtVideoSrTileModel(path, actualBackend, 64, default, 2, true, half);
                        if (!tensor.Device.IsHardware || !tensor.Device.Name.StartsWith("GPU")) throw new InvalidOperationException("Non-GPU session");
                        foreach (string fixture in new[] { "random", "flat", "edges" })
                        {
                            string prefix = Path.Combine(references, Path.GetFileNameWithoutExtension(graph.FileName), fixture);
                            var input = ReadFloats(prefix + "-input.f32");
                            var expected = ReadFloats(prefix + "-output.f32");
                            var actual = new float[3 * 128 * 128];
                            tensor.Infer(input, actual, default);
                            double max = 0, sum = 0;
                            for (int i = 0; i < actual.Length; i++)
                            {
                                if (!float.IsFinite(actual[i])) throw new InvalidOperationException("Non-finite GPU output");
                                double delta = Math.Abs(actual[i] - expected[i]);
                                max = Math.Max(max, delta); sum += delta;
                            }
                            double mae = sum / actual.Length;
                            numeric.Add(new { fixture, max, mae });
                            Console.WriteLine($"NUMERIC {name}/{fixture}: max={max:G6}, mae={mae:G6}");
                            if (max > .02 || mae > .001) throw new InvalidOperationException("Packaged RGB GPU graph differs from FP32 CPU reference");
                        }
                        using var cancelled = new CancellationTokenSource();
                        cancelled.Cancel();
                        try { tensor.Infer(new float[3 * 64 * 64], new float[3 * 128 * 128], cancelled.Token); throw new InvalidOperationException("Cancellation ignored"); }
                        catch (OperationCanceledException) { }
                    }
                    var options = new VideoTranscodeOptions
                    {
                        SuperResolutionEnabled = true, SuperResolutionModel = model.Model,
                        SuperResolutionBackend = backend, SuperResolutionUseFp16 = half,
                        TrimStartSeconds = .4, TrimEndSeconds = 1.1, Preset = PresetLevel.Ultrafast
                    };
                    using (var session = sr.CreateSessionAsync(options).GetAwaiter().GetResult())
                    {
                        Console.WriteLine("BITMAP start " + name);
                        using var input = CreateCandidateFrames(667, 337, 1)[0];
                        using var output = session.Upscale(input, 2, default);
                        if (!session.Device.IsHardware || !session.Device.Name.StartsWith("GPU") || output.Width != 1334 || output.Height != 674
                            || output.Pixels.Distinct().Count() < 100)
                            throw new InvalidOperationException("Production tiling or GPU device incorrect");
                        if (model.RequiresDirectMl && !session.Device.Name.Contains("DirectML")) throw new InvalidOperationException("RAVU selected an unqualified provider");
                        using var image = SKImage.FromBitmap(output);
                        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                        using var stream = File.Create(Path.Combine(workDir, name + ".png"));
                        png.SaveTo(stream);
                        Console.WriteLine("BITMAP done " + name);
                    }
                    Console.WriteLine("BITMAP session disposed " + name);
                    string video = Path.Combine(workDir, name + ".mp4");
                    service.TranscodeAsync(source, video, options).GetAwaiter().GetResult();
                    var probe = service.ProbeAsync(video).GetAwaiter().GetResult()!;
                    var times = ReadVideoTimes(video);
                    if (probe.Width != 320 || probe.Height != 240 || !probe.HasAudio || times.Count != 7
                        || Math.Abs(probe.DurationMs - 700) > 110 || Math.Abs(probe.Fps - 10) > .01)
                        throw new InvalidOperationException($"Wrong enhanced clip: {probe}, frames={times.Count}");
                    records.Add(new { name, numeric, graph.Sha256, frames = times.Count, probe.DurationMs, probe.HasAudio, passed = true });
                    File.WriteAllText(Path.Combine(workDir, "packaged-model-results.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
                });
            }
        }

        if (modelArgument >= 0)
        {
            Console.WriteLine($"GPU SR: {_runCount} cases, {_failures} failures");
            return _failures == 0 ? 0 : 1;
        }

        RunExtra("offline installer repairs both FP32 and FP16 copies", () =>
        {
            var model = VideoSrModelCatalog.Get(VideoSrModel.Anime4KCnnS);
            foreach (var graph in new[] { model.FullPrecision, model.HalfPrecision! })
                File.WriteAllText(Path.Combine(AppPaths.ModelsDirectory, "VideoSr", graph.FileName), "corrupt");
            sr.EnsureModelAsync(model.Model).GetAwaiter().GetResult();
            foreach (var graph in new[] { model.FullPrecision, model.HalfPrecision! })
                if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppPaths.ModelsDirectory, "VideoSr", graph.FileName))))
                        .Equals(graph.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Corrupt bundle was not repaired");
        });
        RunExtra("CPU/NPU and unsupported RAVU FP16 are rejected", () =>
        {
            foreach (var options in new[]
            {
                new VideoTranscodeOptions { SuperResolutionBackend = VideoSrBackend.Cpu },
                new VideoTranscodeOptions { SuperResolutionBackend = VideoSrBackend.PreferNpu },
                new VideoTranscodeOptions { SuperResolutionModel = VideoSrModel.RavuLiteR2, SuperResolutionUseFp16 = true },
                new VideoTranscodeOptions { SuperResolutionModel = VideoSrModel.RavuLiteR3, SuperResolutionUseFp16 = true },
                new VideoTranscodeOptions { SuperResolutionModel = VideoSrModel.RavuLiteR3, SuperResolutionBackend = VideoSrBackend.DirectMl },
                new VideoTranscodeOptions { SuperResolutionModel = VideoSrModel.Anime4KCnnS, SuperResolutionUseFp16 = true, SuperResolutionBackend = VideoSrBackend.DirectMl }
            })
            {
                try { using var invalid = sr.CreateSessionAsync(options).GetAwaiter().GetResult(); throw new InvalidOperationException("Unsupported configuration accepted"); }
                catch (NotSupportedException) { }
            }
        });
        foreach (var model in new[] { VideoSrModel.Anime4KCnnS, VideoSrModel.RavuLiteR2 })
            RunExtra("cascaded 4x with crop and tail timestamps: " + model, () =>
            {
                string output = Path.Combine(workDir, model + "-4x.mp4");
                service.TranscodeAsync(source, output, new VideoTranscodeOptions
                {
                    SuperResolutionEnabled = true, SuperResolutionModel = model, SuperResolutionScale = 4, SuperResolutionBackend = selectedBackend,
                    CropEnabled = true, CropLeft = 4, CropRight = 4, CropTop = 2, CropBottom = 2,
                    TrimStartSeconds = .4, TrimEndSeconds = .7, AudioCodec = AudioCodec.None, Preset = PresetLevel.Ultrafast
                }).GetAwaiter().GetResult();
                var probe = service.ProbeAsync(output).GetAwaiter().GetResult()!;
                if (probe.Width != 608 || probe.Height != 464 || ReadVideoTimes(output).Count != 3 || Math.Abs(probe.Fps - 10) > .01)
                    throw new InvalidOperationException("Cascaded 4x geometry/timestamps incorrect");
            });
        RunExtra("GPU fps preprocessing and final output scaling", () =>
        {
            string output = Path.Combine(workDir, "sr-filtered.mp4");
            service.TranscodeAsync(source, output, new VideoTranscodeOptions
            {
                SuperResolutionEnabled = true, SuperResolutionBackend = selectedBackend, TrimStartSeconds = .4, TrimEndSeconds = 1,
                FpsMode = FpsMode.Fixed, FpsValue = 5, ScaleMode = ScaleMode.Exact, ScaleWidth = 200, ScaleHeight = 150,
                Preset = PresetLevel.Ultrafast
            }).GetAwaiter().GetResult();
            var probe = service.ProbeAsync(output).GetAwaiter().GetResult()!;
            if (probe.Width != 200 || probe.Height != 150 || Math.Abs(probe.Fps - 5) > .01 || Math.Abs(probe.DurationMs - 600) > 150)
                throw new InvalidOperationException("Pre/post filter settings incorrect");
        });
        RunExtra("canceled enhancement removes partial output", () =>
        {
            using var ct = new CancellationTokenSource();
            string output = Path.Combine(workDir, "sr-canceled.mp4");
            try
            {
                service.TranscodeAsync(source, output, new VideoTranscodeOptions
                { SuperResolutionEnabled = true, SuperResolutionBackend = selectedBackend, Preset = PresetLevel.Ultrafast }, ct: ct.Token, preview: new CancelPreview(ct)).GetAwaiter().GetResult();
                throw new InvalidOperationException("Cancellation ignored");
            }
            catch (OperationCanceledException) { }
            if (File.Exists(output) || File.Exists(Path.ChangeExtension(output, "tmp.mp4"))) throw new InvalidOperationException("Canceled output was retained");
        });
        RunExtra("exclusive lease cancellation, reuse after cancellation and early 4x size guard", () =>
        {
            var options = new VideoTranscodeOptions();
            VideoSrDevice firstDevice;
            using (var first = sr.CreateSessionAsync(options).GetAwaiter().GetResult())
            {
                firstDevice = first.Device;
                using var blocked = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                try { using var invalid = sr.CreateSessionAsync(options, blocked.Token).GetAwaiter().GetResult(); throw new InvalidOperationException("Concurrent lease was granted"); }
                catch (OperationCanceledException) { }
            }
            using var second = sr.CreateSessionAsync(options).GetAwaiter().GetResult();
            if (!ReferenceEquals(firstDevice, second.Device)) throw new InvalidOperationException("Compiled session was not reused");
            using var sourceFrame = CreateCandidateFrames(17, 9, 1)[0];
            using var output = second.Upscale(sourceFrame, 2, default);
            if (output.Width != 34 || output.Height != 18) throw new InvalidOperationException("Session was unusable after canceled conversion");
            using var oversized = new SKBitmap(new SKImageInfo(3000, 1, SKColorType.Bgra8888, SKAlphaType.Opaque));
            bool rejected = false;
            try { using var invalid = second.Upscale(oversized, 4, default); }
            catch (InvalidOperationException ex) when (ex.Message == LocalizationRegistry.Get("VideoTranscode.Msg_SrSizeLimit")) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("4x output limit was not checked before inference");
        });
        File.WriteAllText(Path.Combine(workDir, "packaged-model-results.json"), JsonSerializer.Serialize(new
        { passed = _failures == 0, cases = _runCount, failures = _failures, cpuFallbackDisabled = true, references, records,
            processPeakWorkingSetMiB = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"GPU SR: {_runCount} cases, {_failures} failures");
        return _failures == 0 ? 0 : 1;
    }

    private sealed class CancelPreview(CancellationTokenSource source) : IProgress<VideoPreviewFrame>
    {
        /// <inheritdoc />
        public void Report(VideoPreviewFrame frame) => source.Cancel();
    }
}
