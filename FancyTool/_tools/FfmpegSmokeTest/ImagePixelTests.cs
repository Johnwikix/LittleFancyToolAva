using System.Runtime.Intrinsics.X86;
using FancyToolAva.Services;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    // 对照迁移前逐像素算法，覆盖 SIMD 分组、尾部、非紧凑行与所有 Half 位模式。
    private static int RunImagePixelCases()
    {
        try
        {
            int assertions = 0;
            byte Quantize(float value) => (byte)(Math.Clamp(value, 0f, 1f) * 255f);
            void Equal(byte actual, byte expected)
            {
                assertions++;
                if (actual != expected) throw new InvalidOperationException($"pixel #{assertions}: {actual} != {expected}");
            }
            foreach (int channels in new[] { 1, 2, 3 })
            foreach (int width in new[] { 1, 3, 4, 7, 8, 13, 257 })
            {
                int height = (65536 + width - 1) / width;
                int plane = width * height;
                var half = new Half[plane * channels];
                for (int i = 0; i < half.Length; i++) half[i] = BitConverter.UInt16BitsToHalf((ushort)i);
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var target = new SKBitmap(info, width * 4 + 16);
                target.GetPixelSpan().Fill(0x5a);
                ImageTensorPixels.ReadHalf(half, target, width, height, channels);
                var pixels = target.GetPixelSpan();
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * width + x, p = y * target.RowBytes + x * 4;
                        Equal(pixels[p], Quantize((float)half[(channels >= 3 ? plane * 2 : 0) + i]));
                        Equal(pixels[p + 1], Quantize((float)half[(channels >= 2 ? plane : 0) + i]));
                        Equal(pixels[p + 2], Quantize((float)half[i]));
                        Equal(pixels[p + 3], 255);
                    }
                    if (y + 1 < height)
                        for (int p = width * 4; p < target.RowBytes; p++) Equal(pixels[y * target.RowBytes + p], 0x5a);
                }
            }
            // 单精度量化边界：每个字节阈值及其相邻浮点数、NaN、无穷和越界值。
            var values = new List<float> { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f, -0f, 0f, 1f, 2f };
            for (int i = 0; i <= 255; i++)
            {
                float value = i / 255f;
                values.AddRange([MathF.BitDecrement(value), value, MathF.BitIncrement(value)]);
            }
            int floatWidth = values.Count;
            var floats = Enumerable.Range(0, floatWidth * 3).Select(i => values[(i % floatWidth + i / floatWidth * 17) % floatWidth]).ToArray();
            using (var target = new SKBitmap(floatWidth, 1, SKColorType.Bgra8888, SKAlphaType.Premul))
            {
                ImageTensorPixels.ReadFloat(floats, target, floatWidth, 1, 3);
                var pixels = target.GetPixelSpan();
                for (int x = 0; x < floatWidth; x++)
                for (int c = 0; c < 4; c++) Equal(pixels[x * 4 + c], c == 3 ? (byte)255 : Quantize(floats[(2 - c) * floatWidth + x]));
            }
            // 256×256 全组合证明除以 255 的整数变换精确；独立通道、行填充和尾部同时覆盖。
            foreach (int width in new[] { 1, 3, 4, 7, 8, 13, 257 })
            {
                int height = (65536 + width - 1) / width;
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var rgb = new SKBitmap(info, width * 4 + 16);
                using var alpha = new SKBitmap(info, width * 4 + 32);
                rgb.GetPixelSpan().Fill(0x5a);
                var pixels = rgb.GetPixelSpan();
                var opacity = alpha.GetPixelSpan();
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x, p = y * rgb.RowBytes + x * 4;
                    pixels[p] = (byte)i;
                    pixels[p + 1] = (byte)(255 - i % 256);
                    pixels[p + 2] = (byte)(i * 17);
                    pixels[p + 3] = 73;
                    opacity[y * alpha.RowBytes + x * 4 + 3] = (byte)(i / 256);
                }
                ImageTensorPixels.MergeAlpha(rgb, alpha);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * width + x, p = y * rgb.RowBytes + x * 4;
                        byte a = (byte)(i / 256);
                        Equal(pixels[p], (byte)((byte)i * a / 255));
                        Equal(pixels[p + 1], (byte)((255 - i % 256) * a / 255));
                        Equal(pixels[p + 2], (byte)((byte)(i * 17) * a / 255));
                        Equal(pixels[p + 3], a);
                    }
                    if (y + 1 < height)
                        for (int p = width * 4; p < rgb.RowBytes; p++) Equal(pixels[y * rgb.RowBytes + p], 0x5a);
                }
            }
            Console.WriteLine($"Image pixels OK: {assertions} comparisons, AVX2={Avx2.IsSupported}, SSE2={Sse2.IsSupported}");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("Image pixels FAIL: " + error); return 1; }
    }
}
