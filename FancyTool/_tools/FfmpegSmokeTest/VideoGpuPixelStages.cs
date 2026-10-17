using System.Runtime.InteropServices;
using System.Text.Json;
using FancyToolAva.Services;
using Microsoft.ML.OnnxRuntime;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static unsafe int RunGpuPixelStages(string workDir)
    {
        string source = Path.Combine(AppPaths.ModelsDirectory, "Interpolation/rife_v4.25_lite_v2.onnx");
        var paths = VideoPixelGraph.CreateStages(source, "rife", default);
        var values = new List<float> { float.NaN, float.PositiveInfinity, float.NegativeInfinity,
            float.MaxValue, -float.MaxValue, -1, 0, 1, 2 };
        for (int i = 0; i < 255; i++)
        {
            float value = (float)((i + .5) / 255);
            values.Add(value); values.Add(MathF.BitIncrement(value)); values.Add(MathF.BitDecrement(value));
            values.Add(MathF.BitDecrement(MathF.BitDecrement(value)));
            values.Add(MathF.BitIncrement(MathF.BitIncrement(value)));
        }
        int width = values.Count;
        var input = new float[width * 3];
        for (int i = 0; i < input.Length; i++) input[i] = values[i % width];
        var output = new int[width];
        using var session = WindowsMlAccelerator.CreateSession(paths.After, VideoSrBackend.VendorGpu, width, 1, default, out var device, true);
        using var tensor = OrtValue.CreateTensorValueFromMemory(input, [1, 3, 1, width]);
        using var result = OrtValue.CreateTensorValueFromMemory(output, [1, 1, width]);
        using var run = new RunOptions();
        session.Run(run, [session.InputNames.Single()], [tensor], [session.OutputNames.Single()], [result]);
        var native = new int[width];
        using (var cuda = new CudaVideoContext(device.TensorRtDeviceId!.Value))
        using (var gpuInput = cuda.Allocate(width * 3, 1))
        using (var gpuOutput = cuda.Allocate(width, 1))
        {
            using (cuda.Enter())
            fixed (float* cpu = input) CudaVideoContext.Check(CudaUpload((ulong)gpuInput.Frame.Data, cpu, (nuint)(input.Length * 4)));
            cuda.PackRife(gpuInput.Frame.Data, width, 1, gpuOutput.Frame);
            using (cuda.Enter())
            fixed (int* cpu = native) CudaVideoContext.Check(CudaDownload(cpu, (ulong)gpuOutput.Frame.Data, (nuint)(native.Length * 4)));
        }
        var nativeMismatches = Enumerable.Range(0, width).Where(i => native[i] != output[i]).ToArray();
        if (nativeMismatches.Length != 0) throw new InvalidOperationException("CUDA quantizer differs from GPU graph: " + string.Join(",", nativeMismatches.Take(20)));
        Console.WriteLine($"CUDA quantization: {width} values, {nativeMismatches.Length} mismatches");
        var mismatches = new List<object>();
        for (int i = 0; i < width; i++)
        {
            byte v = (byte)Math.Clamp(float.IsFinite(input[i]) ? input[i] * 255f + .5f : 0, 0, 255);
            int expected = unchecked((int)(0xff000000u | (uint)(v * 0x010101)));
            if (output[i] != expected) mismatches.Add(new { i, input = input[i].ToString("R"), expected, actual = output[i] });
        }
        Console.WriteLine($"GPU quantization: {width} values, {mismatches.Count} mismatches; {device.Name}");
        foreach (var mismatch in mismatches.Take(20)) Console.WriteLine(JsonSerializer.Serialize(mismatch));
        File.WriteAllText(Path.Combine(workDir, "quantizer.json"), JsonSerializer.Serialize(new { width, mismatches, nativeMismatches }));
        return mismatches.Count == 0 ? 0 : 1;
    }
}
