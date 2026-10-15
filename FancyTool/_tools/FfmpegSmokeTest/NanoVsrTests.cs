using System.Diagnostics;
using System.Runtime.InteropServices;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunNanoVsrCases(string source, string workDir)
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        using var http = new HttpClient();
        var downloads = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
        var sr = new VideoSuperResolutionService(downloads, NullLogger<VideoSuperResolutionService>.Instance);
        var interpolation = new VideoInterpolationService(downloads, NullLogger<VideoInterpolationService>.Instance);
        var service = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance, sr, interpolation);
        sr.DeviceChanged += device => Console.WriteLine($"  NanoVSR device: {device}");
        RunExtra("vector BGRA input matches scalar normalization and edge padding", CheckSrPixelInput);
        RunExtra("vector pixel packing agrees with scalar RGB, downsampling and clamping", CheckSrPixelPacking);
        RunExtra("TensorRT catalog and built-in spelling both match", () =>
        {
            if (!WindowsMlAccelerator.IsTensorRtProvider("NvTensorRTRTXExecutionProvider")
                || !WindowsMlAccelerator.IsTensorRtProvider("NvTensorRtRtxExecutionProvider")
                || WindowsMlAccelerator.IsTensorRtProvider("CPUExecutionProvider"))
                throw new InvalidOperationException("TensorRT provider name recognition regressed");
        });
        RunExtra("bundled NanoVSR install repairs a corrupt copy", () =>
        {
            sr.EnsureModelAsync(SuperResolutionModel.NanoVsr644K).GetAwaiter().GetResult();
            string path = Path.Combine(AppPaths.ModelsDirectory, ModelManifest.NanoVsr.FileName);
            File.WriteAllText(path, "corrupt test fixture");
            sr.EnsureModelAsync(SuperResolutionModel.NanoVsr644K).GetAwaiter().GetResult();
            if (new FileInfo(path).Length < 1_000_000 || File.Exists(path + ".partial")) throw new InvalidOperationException("Model repair failed");
        });
        RunExtra("TensorRT NanoVSR agrees with exported PyTorch reference", () =>
        {
            string references = Path.Combine(AppContext.BaseDirectory, "NanoVsrReference");
            float[] input = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(references, "input.f32"))).ToArray();
            float[] expected = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(references, "output.f32"))).ToArray();
            var half = new Float16[input.Length];
            for (int i = 0; i < input.Length; i++) half[i] = (Float16)input[i];
            using var session = WindowsMlAccelerator.CreateTensorSession(Path.Combine(AppPaths.ModelsDirectory, ModelManifest.NanoVsr.FileName),
                VideoSrBackend.Gpu, new Dictionary<string, long> { ["height"] = 12, ["width"] = 16 }, default, out var device, true);
            Console.WriteLine("  parity provider: " + device.Name);
            using var tensor = OrtValue.CreateTensorValueFromMemory(half, [1, 5, 3, 12, 16]);
            using var options = new RunOptions();
            using var result = session.Run(options, new Dictionary<string, OrtValue> { ["lr"] = tensor }, ["sr"]);
            var actual = result[0].GetTensorDataAsSpan<Float16>();
            if (actual.Length != expected.Length) throw new InvalidOperationException("Wrong temporal tensor shape");
            float error = 0;
            for (int i = 0; i < actual.Length; i++) error = Math.Max(error, Math.Abs((float)actual[i] - expected[i]));
            Console.WriteLine($"  PyTorch/GPU max absolute error: {error}");
            if (error > .008f) throw new InvalidOperationException("GPU inference deviates from the PyTorch reference");
        });
        RunExtra("temporal neighbours affect the middle output; partial window preserves count", () =>
        {
            using var session = new OrtNanoVsrUpscaler(Path.Combine(AppPaths.ModelsDirectory, ModelManifest.NanoVsr.FileName), VideoSrBackend.Gpu, 80, default);
            var inputs = Enumerable.Range(0, 5).Select(_ => new SKBitmap(new SKImageInfo(13, 11, SKColorType.Bgra8888, SKAlphaType.Opaque))).ToArray();
            try
            {
                var random = new Random(710);
                foreach (var frame in inputs)
                {
                    var pixels = frame.GetPixelSpan();
                    random.NextBytes(pixels);
                    for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                }
                var original = session.UpscaleSequence(inputs, 2, default);
                foreach (int i in new[] { 0, 1, 3, 4 }) inputs[i].Erase(SKColors.Black);
                var changed = session.UpscaleSequence(inputs, 2, default);
                try
                {
                    if (original[2].GetPixelSpan().SequenceEqual(changed[2].GetPixelSpan())) throw new InvalidOperationException("NanoVSR ignored temporal neighbours");
                }
                finally { foreach (var frame in original.Concat(changed)) frame.Dispose(); }
                var tail = session.UpscaleSequence(inputs.Take(2).ToArray(), 4, default);
                try { if (tail.Length != 2 || tail[0].Width != 52 || tail[0].Height != 44) throw new InvalidOperationException("Partial window output incorrect"); }
                finally { foreach (var frame in tail) frame.Dispose(); }
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                try { session.UpscaleSequence(inputs, 2, cancellation.Token); throw new InvalidOperationException("Cancellation ignored"); }
                catch (OperationCanceledException) { }
            }
            finally { foreach (var frame in inputs) frame.Dispose(); }
        });
        RunExtra("NanoVSR explicit CPU fallback executes the same temporal model", () =>
        {
            using var session = new OrtNanoVsrUpscaler(Path.Combine(AppPaths.ModelsDirectory, ModelManifest.NanoVsr.FileName), VideoSrBackend.Cpu, 80, default);
            using var input = new SKBitmap(new SKImageInfo(13, 11, SKColorType.Bgra8888, SKAlphaType.Opaque));
            input.Erase(SKColors.SteelBlue);
            using var output = session.Upscale(input, 2, default);
            if (session.Device.IsHardware || output.Width != 26 || output.Height != 22) throw new InvalidOperationException("CPU fallback failed");
        });
        foreach (int count in new[] { 1, 3, 5, 7 })
        RunExtra($"NanoVSR transcode {count} frames: exact clock, tail and audio", () =>
        {
            string output = Path.Combine(workDir, $"nano-{count}.mp4");
            service.TranscodeAsync(source, output, NanoOptions(count), ct: default).GetAwaiter().GetResult();
            AssertInterpolated(service, output, 10, count, count / 10d, true);
            var probe = service.ProbeAsync(output).GetAwaiter().GetResult()!;
            if (probe.Width != 320 || probe.Height != 240) throw new InvalidOperationException("Wrong video output dimensions");
        });
        RunExtra("NanoVSR crop, 4x and output scaling", () =>
        {
            var options = NanoOptions(3); options.CropEnabled = true; options.CropLeft = options.CropRight = 4;
            options.CropTop = options.CropBottom = 2; options.SuperResolutionScale = 4;
            string output = Path.Combine(workDir, "nano-cropped.mp4");
            service.TranscodeAsync(source, output, options).GetAwaiter().GetResult();
            var probe = service.ProbeAsync(output).GetAwaiter().GetResult()!;
            if (probe.Width != 608 || probe.Height != 464) throw new InvalidOperationException("NanoVSR crop/4x failed");
            AssertInterpolated(service, output, 10, 3, .3, true);
        });
        foreach (var order in Enum.GetValues<VideoFrameProcessingOrder>())
        RunExtra($"NanoVSR plus motion interpolation: {order}", () =>
        {
            var options = NanoOptions(3); options.InterpolationMode = VideoInterpolationMode.MotionCompensation;
            options.InterpolationMultiplier = 2; options.FrameProcessingOrder = order;
            string output = Path.Combine(workDir, $"nano-interpolation-{order}.mp4");
            service.TranscodeAsync(source, output, options).GetAwaiter().GetResult();
            AssertInterpolated(service, output, 20, 6, .3, true);
        });
        RunExtra("NanoVSR canceled transcode removes partial output", () =>
        {
            using var cancellation = new CancellationTokenSource();
            string output = Path.Combine(workDir, "nano-canceled.mp4");
            try
            {
                service.TranscodeAsync(source, output, NanoOptions(16), ct: cancellation.Token, preview: new CancelPreview(cancellation)).GetAwaiter().GetResult();
                throw new InvalidOperationException("Cancellation ignored");
            }
            catch (OperationCanceledException) { }
            if (File.Exists(output) || File.Exists(Path.ChangeExtension(output, "tmp.mp4"))) throw new InvalidOperationException("Canceled conversion retained output");
        });
        Console.WriteLine($"NanoVSR: {_runCount} tests, {_failures} failures");
        return _failures == 0 ? 0 : 1;
    }

    private static VideoTranscodeOptions NanoOptions(int frames) => new()
    {
        SuperResolutionEnabled = true, SuperResolutionModel = SuperResolutionModel.NanoVsr644K,
        SuperResolutionBackend = VideoSrBackend.Gpu, SuperResolutionAllowCpuFallback = false,
        TrimStartSeconds = .4, TrimEndSeconds = .4 + frames / 10d, Preset = PresetLevel.Ultrafast
    };

    private static void CheckSrPixelPacking()
    {
        const int window = 80, pad = 32, width = 13, height = 11;
        foreach (var scales in new[] { (2, 2), (4, 2), (4, 4) })
        {
            int native = scales.Item1, scale = scales.Item2, stride = window * native;
            int channel = stride * stride, step = native / scale;
            var tensor = new float[3 * channel];
            var random = new Random(12345);
            for (int i = 0; i < tensor.Length; i++) tensor[i] = (float)(random.NextDouble() * 1.4 - .2);
            using var actual = new SKBitmap(new SKImageInfo(width * scale, height * scale, SKColorType.Bgra8888, SKAlphaType.Opaque));
            VideoFrameUpscaler.PasteOutput(actual, 0, 0, width, height, scale, window, pad, native, tensor);
            var pixels = actual.GetPixelSpan();
            for (int row = 0; row < height * scale; row++)
            for (int col = 0; col < width * scale; col++)
            for (int c = 0; c < 3; c++)
            {
                int source = c * channel + (pad * native + row * step) * stride + pad * native + col * step;
                float value = tensor[source];
                if (step == 2) value = (value + tensor[source + 1] + tensor[source + stride] + tensor[source + stride + 1]) * .25f;
                byte expected = (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
                if (pixels[row * actual.RowBytes + col * 4 + 2 - c] != expected)
                    throw new InvalidOperationException($"Pixel mismatch at ({row},{col},{c}) for {native}x -> {scale}x");
            }
        }
    }

    private static void CheckSrPixelInput()
    {
        const int window = 80, pad = 32, channel = window * window;
        using var input = new SKBitmap(new SKImageInfo(113, 89, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var pixels = input.GetPixelSpan();
        new Random(12345).NextBytes(pixels);
        var tensor = new float[3 * channel];
        foreach (var origin in new[] { (0, 0), (64, 64), (110, 87) })
        {
            VideoFrameUpscaler.FillInput(input, origin.Item1, origin.Item2, window, pad, tensor);
            for (int row = 0; row < window; row++)
            for (int col = 0; col < window; col++)
            for (int c = 0; c < 3; c++)
            {
                int source = Math.Clamp(origin.Item2 + row - pad, 0, input.Height - 1) * input.RowBytes
                    + Math.Clamp(origin.Item1 + col - pad, 0, input.Width - 1) * 4 + 2 - c;
                if (tensor[c * channel + row * window + col] != pixels[source] / 255f)
                    throw new InvalidOperationException("Vector input conversion differs from scalar padding/normalization");
            }
        }
    }
}
