using System.Diagnostics;
using System.Text.Json;
using FancyToolAva.Services;
using Microsoft.ML.OnnxRuntime;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunGpuPixelBatchExperiment(string workDir)
    {
        var results = new List<object>();
        const int window = 384;
        foreach (int batch in new[] { 1, 2, 4, 8 })
        {
            using var session = WindowsMlAccelerator.CreateTensorSession(
                $".qa/video-pipeline/gpu-pixels/fsrcnnx_m_bgra_batch{batch}.onnx", VideoSrBackend.VendorGpu,
                new Dictionary<string, long> { ["height"] = window, ["width"] = window, ["batch"] = batch, ["batch_size"] = batch },
                default, out var device, true);
            byte[] input = new byte[batch * window * window * 4], output = new byte[batch * window * window * 16];
            new Random(123).NextBytes(input);
            using var i = OrtValue.CreateTensorValueFromMemory(input, [batch, window, window, 4]);
            using var o = OrtValue.CreateTensorValueFromMemory(output, [batch, window * 2, window * 2, 4]);
            string[] inputNames = [session.InputNames.Single()], outputNames = [session.OutputNames.Single()];
            OrtValue[] inputs = [i], outputs = [o];
            using var run = new RunOptions();
            session.Run(run, inputNames, inputs, outputNames, outputs);
            File.WriteAllBytes(Path.Combine(workDir, $"batch{batch}.bgra"), output);
            var timer = Stopwatch.StartNew();
            for (int repeat = 0; repeat < 100; repeat++) session.Run(run, inputNames, inputs, outputNames, outputs);
            var result = new { batch, perTileMs = timer.Elapsed.TotalMilliseconds / (100 * batch), device.Name };
            Console.WriteLine(JsonSerializer.Serialize(result)); results.Add(result);
        }
        File.WriteAllText(Path.Combine(workDir, "batch.json"), JsonSerializer.Serialize(results));
        return 0;
    }
}
