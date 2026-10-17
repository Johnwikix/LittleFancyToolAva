using System.Diagnostics;
using System.Text.Json;
using FancyToolAva.Services;
using Microsoft.ML.OnnxRuntime;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunGpuPixelExperiment(string workDir)
    {
        if (Environment.GetEnvironmentVariable("FANCYTOOL_TEST_RIFE_PIXELS") == "1") return RunRifePixelExperiment(workDir);
        const int window = 384;
        var records = new List<object>();
        using var original = new OrtVideoSrTileModel("FancyTool/Libraries/VideoSr/fsrcnnx_m_fp16.onnx",
            VideoSrBackend.VendorGpu, window, default, 2, true, true);
        using var session = WindowsMlAccelerator.CreateSession(".qa/video-pipeline/gpu-pixels/fsrcnnx_m_bgra.onnx",
            VideoSrBackend.VendorGpu, window, default, out var device);
        Console.WriteLine("BGRA DEVICE " + device);
        var input = new byte[window * window * 4];
        var output = new byte[window * window * 16];
        using var inputTensor = OrtValue.CreateTensorValueFromMemory(input, [1, window, window, 4]);
        using var outputTensor = OrtValue.CreateTensorValueFromMemory(output, [1, window * 2, window * 2, 4]);
        using var run = new RunOptions();
        string[] names = [session.InputNames.Single()], outputs = [session.OutputNames.Single()];
        OrtValue[] inputs = [inputTensor], values = [outputTensor];
        var floats = new float[window * window * 3];
        var reference = new float[window * window * 12];
        for (int fixture = 0; fixture < 4; fixture++)
        {
            new Random(123 + fixture).NextBytes(input);
            int plane = window * window;
            for (int i = 0; i < plane; i++)
            {
                floats[i] = input[i * 4 + 2] / 255f;
                floats[i + plane] = input[i * 4 + 1] / 255f;
                floats[i + plane * 2] = input[i * 4] / 255f;
            }
            original.Infer(floats, reference, default);
            session.Run(run, names, inputs, outputs, values);
            int max = 0, different = 0; long total = 0;
            for (int i = 0; i < plane * 4; i++)
            for (int channel = 0; channel < 3; channel++)
            {
                int expected = Math.Clamp((int)MathF.Round(reference[channel * plane * 4 + i] * 255), 0, 255);
                int delta = Math.Abs(expected - output[i * 4 + 2 - channel]);
                max = Math.Max(max, delta); total += delta; if (delta != 0) different++;
            }
            var record = new { fixture, max, different, mean = total / (plane * 12d) };
            Console.WriteLine(JsonSerializer.Serialize(record)); records.Add(record);
        }
        double Measure(Action action)
        {
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) action();
            return timer.Elapsed.TotalMilliseconds / 100;
        }
        var timing = new { originalMs = Measure(() => original.Infer(floats, reference, default)),
            bgraMs = Measure(() => session.Run(run, names, inputs, outputs, values)) };
        records.Add(timing); Console.WriteLine(JsonSerializer.Serialize(timing));
        File.WriteAllText(Path.Combine(workDir, "experiment.json"), JsonSerializer.Serialize(records));
        return 0;
    }

    private static int RunRifePixelExperiment(string workDir)
    {
        string path = Path.Combine(AppPaths.ModelsDirectory, "Interpolation/rife_v4.25_lite_v2.onnx");
        var records = new List<object>();
        foreach (var (width, height) in new[] { (17, 9), (160, 120), (640, 360), (1920, 1080) })
        {
            var frames = CreateCandidateFrames(width, height, 2);
            try
            {
                using var reference = new OrtVideoFrameInterpolator(path, VideoSrBackend.Gpu, width, height, default);
                using var adapted = new OrtBgraVideoFrameInterpolator(path, width, height, default, null);
                using var before = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                using var after = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                reference.SetFrames(frames[0], frames[1], default); adapted.SetFrames(frames[0], frames[1], default);
                foreach (double amount in new[] { .25, .5, .75 })
                {
                    reference.Interpolate(amount, before, default); adapted.Interpolate(amount, after, default);
                    var a = before.GetPixelSpan(); var b = after.GetPixelSpan();
                    int max = 0, different = 0; long total = 0;
                    for (int i = 0; i < a.Length; i++)
                    {
                        int d = Math.Abs(a[i] - b[i]); max = Math.Max(max, d); total += d; if (d > 0) different++;
                    }
                    var result = new { width, height, amount, max, different, mean = total / (double)a.Length };
                    records.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
                }
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
        File.WriteAllText(Path.Combine(workDir, "rife-experiment.json"), JsonSerializer.Serialize(records));
        return 0;
    }
}
