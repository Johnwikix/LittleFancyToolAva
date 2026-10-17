using System.Diagnostics;
using System.Text.Json;
using FancyToolAva.Services;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunVideoPipelineCpuTests(string workDir)
    {
        foreach (var (width, height, paddedWidth, paddedHeight) in new[] { (1, 1, 128, 128), (17, 9, 17, 9), (63, 65, 128, 65), (1920, 1080, 1920, 1080) })
        {
            using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            new Random(127).NextBytes(bitmap.GetPixelSpan());
            float[] expected = new float[3 * paddedWidth * paddedHeight], actual = new float[expected.Length];
            ScalarPack(bitmap, paddedWidth, paddedHeight, expected);
            VideoTensorPixels.Pack(bitmap, paddedWidth, paddedHeight, actual, default);
            if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("Pixel input mismatch");
            float[] special = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, -float.MaxValue, -.1f, 1.1f, .5f, 0, 1, .5f / 255, 1.5f / 255];
            for (int i = 0; i < actual.Length; i++) actual[i] = i < special.Length ? special[i] : (i * 37 % 1024) / 1024f * 1.2f - .1f;
            using var output = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            VideoTensorPixels.Unpack(actual, paddedWidth, paddedHeight, output, default);
            int plane = paddedWidth * paddedHeight;
            var pixels = output.GetPixelSpan();
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            for (int c = 0; c < 4; c++)
            {
                float v = c == 3 ? 1 : actual[(2 - c) * plane + y * paddedWidth + x];
                byte value = (byte)Math.Clamp(float.IsFinite(v) ? v * 255f + .5f : 0, 0, 255);
                if (pixels[y * output.RowBytes + x * 4 + c] != value) throw new InvalidOperationException("Pixel output mismatch");
            }
            Console.WriteLine($"PASS pixel reference {width}x{height}, padded {paddedWidth}x{paddedHeight}");
        }

        using var source = new SKBitmap(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var target = new SKBitmap(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Opaque);
        float[] tensor = new float[3 * 1920 * 1080];
        var timings = new List<object>();
        foreach (bool vector in new[] { false, true })
        {
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++)
                if (vector) VideoTensorPixels.Pack(source, 1920, 1080, tensor, default); else ScalarPack(source, 1920, 1080, tensor);
            double packMs = timer.Elapsed.TotalMilliseconds / 50;
            timer.Restart();
            for (int i = 0; i < 50; i++)
                if (vector) VideoTensorPixels.Unpack(tensor, 1920, 1080, target, default); else ScalarUnpack(tensor, target);
            timings.Add(new { vector, packMs, unpackMs = timer.Elapsed.TotalMilliseconds / 50 });
        }
        File.WriteAllText(Path.Combine(workDir, "pixel-timings.json"), JsonSerializer.Serialize(timings));
        Console.WriteLine(JsonSerializer.Serialize(timings));
        TestBoundedWorkPipeline();
        TestBgraByteBuffers();
        return 0;
    }

    private static void TestBgraByteBuffers()
    {
        foreach (var (width, height) in new[] { (1, 1), (17, 9), (63, 65), (193, 129) })
        {
            using var source = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            new Random(998).NextBytes(source.GetPixelSpan());
            byte[] padded = new byte[192 * 192 * 4];
            VideoBgraPixels.CopyPadded(source, 0, 0, 192, 192, 32, padded, default);
            for (int y = 0; y < 192; y++)
            for (int x = 0; x < 192; x++)
            for (int c = 0; c < 4; c++)
                if (padded[(y * 192 + x) * 4 + c] != source.GetPixelSpan()[Math.Clamp(y - 32, 0, height - 1) * source.RowBytes + Math.Clamp(x - 32, 0, width - 1) * 4 + c])
                    throw new InvalidOperationException("BGRA edge extension mismatch");
            int stride = (width + 3) * 4;
            byte[] previous = new byte[stride * height];
            VideoBgraPixels.CopyPadded(source, 0, 0, width + 3, height, 0, previous, default);
            if (!VideoBgraPixels.Matches(source, previous, stride, default)) throw new InvalidOperationException("Identical pixels rejected");
            int changed = Math.Min(height - 1, height / 2 + 1) * stride + Math.Min(width - 1, width / 2 + 1) * 4;
            previous[changed] ^= 31;
            if (VideoBgraPixels.Matches(source, previous, stride, default)) throw new InvalidOperationException("Unsampled pixel change was missed");
            Console.WriteLine($"PASS BGRA byte buffers {width}x{height}");
        }
    }

    private static void ScalarPack(SKBitmap bitmap, int width, int height, Span<float> result)
    {
        var pixels = bitmap.GetPixelSpan();
        int plane = width * height;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int pixel = Math.Min(y, bitmap.Height - 1) * bitmap.RowBytes + Math.Min(x, bitmap.Width - 1) * 4;
            int i = y * width + x;
            result[i] = pixels[pixel + 2] / 255f;
            result[i + plane] = pixels[pixel + 1] / 255f;
            result[i + plane * 2] = pixels[pixel] / 255f;
        }
    }

    private static void ScalarUnpack(ReadOnlySpan<float> values, SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan();
        int plane = bitmap.Width * bitmap.Height;
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            int i = y * bitmap.Width + x, offset = y * bitmap.RowBytes + x * 4;
            for (int c = 0; c < 3; c++)
            {
                float v = values[(2 - c) * plane + i];
                pixels[offset + c] = (byte)Math.Clamp(float.IsFinite(v) ? v * 255f + .5f : 0, 0, 255);
            }
            pixels[offset + 3] = 255;
        }
    }

    private sealed class PipelineTestBuffer { internal int Value; }
    private static void TestBoundedWorkPipeline()
    {
        var buffers = new[] { new PipelineTestBuffer(), new PipelineTestBuffer(), new PipelineTestBuffer() };
        for (int failingStage = -1; failingStage < 3; failingStage++)
        {
            int prepared = 0, written = 0, active = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                BoundedVideoWorkPipeline.Run(buffers, (b, token) =>
                {
                    if (prepared == 100) return false;
                    b.Value = prepared++;
                    if (failingStage == 0 && b.Value == 9) throw new InvalidDataException("prepare-fault");
                    return true;
                }, (b, token) =>
                {
                    if (Interlocked.Increment(ref active) != 1) throw new InvalidOperationException("Concurrent native session use");
                    Interlocked.Decrement(ref active);
                    if (failingStage == 1 && b.Value == 9) throw new InvalidDataException("infer-fault");
                }, (b, token) =>
                {
                    if (failingStage == 2 && b.Value == 9) throw new InvalidDataException("write-fault");
                    if (b.Value != written++) throw new InvalidOperationException("Buffer reused before write or wrong order");
                }, timeout.Token);
                if (failingStage >= 0 || written != 100) throw new InvalidOperationException("Missing failure or output");
            }
            catch (InvalidDataException) when (failingStage >= 0) { }
            Console.WriteLine($"PASS bounded pipeline stage {failingStage}");
        }
        using var cancelled = new CancellationTokenSource();
        int callbacks = 0;
        try
        {
            BoundedVideoWorkPipeline.Run(buffers, (b, token) => { b.Value = 1; return true; },
                (b, token) => { Interlocked.Increment(ref callbacks); cancelled.Cancel(); token.ThrowIfCancellationRequested(); },
                (b, token) => throw new InvalidOperationException("Write after cancellation"), cancelled.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException) { }
        int atReturn = callbacks;
        Thread.Sleep(20);
        if (callbacks != atReturn) throw new InvalidOperationException("Worker outlived cancellation");
        Console.WriteLine("PASS bounded pipeline cancellation joins all workers");
    }
}
