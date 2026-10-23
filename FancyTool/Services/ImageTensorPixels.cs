using System.Buffers;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>图片模型保持原有截断量化；逐行转换 FP16，避免逐像素软件转换及整幅浮点副本。</summary>
internal static class ImageTensorPixels
{
    internal static void ReadHalf(ReadOnlySpan<Half> source, SKBitmap destination, int width, int height, int channels)
    {
        float[] row = ArrayPool<float>.Shared.Rent(checked(width * 3));
        try
        {
            int plane = checked(width * height);
            var pixels = destination.GetPixelSpan();
            for (int y = 0; y < height; y++)
            {
                int offset = y * width;
                TensorPrimitives.ConvertToSingle(source.Slice(offset, width), row.AsSpan(0, width));
                TensorPrimitives.ConvertToSingle(source.Slice((channels >= 2 ? plane : 0) + offset, width), row.AsSpan(width, width));
                TensorPrimitives.ConvertToSingle(source.Slice((channels >= 3 ? plane * 2 : 0) + offset, width), row.AsSpan(width * 2, width));
                WriteRow(row.AsSpan(0, width), row.AsSpan(width, width), row.AsSpan(width * 2, width),
                    pixels.Slice(y * destination.RowBytes, width * 4));
            }
        }
        finally { ArrayPool<float>.Shared.Return(row); }
    }

    internal static void ReadFloat(ReadOnlySpan<float> source, SKBitmap destination, int width, int height, int channels)
    {
        int plane = checked(width * height);
        var pixels = destination.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            int offset = y * width;
            WriteRow(source.Slice(offset, width), source.Slice((channels >= 2 ? plane : 0) + offset, width),
                source.Slice((channels >= 3 ? plane * 2 : 0) + offset, width), pixels.Slice(y * destination.RowBytes, width * 4));
        }
    }

    internal static void MergeAlpha(SKBitmap rgb, SKBitmap alpha)
    {
        var pixels = rgb.GetPixelSpan();
        var opacity = alpha.GetPixelSpan();
        for (int y = 0; y < rgb.Height; y++)
        {
            ref byte target = ref MemoryMarshal.GetReference(pixels.Slice(y * rgb.RowBytes));
            ref byte source = ref MemoryMarshal.GetReference(opacity.Slice(y * alpha.RowBytes));
            int x = 0;
            if (Avx2.IsSupported)
            {
                for (; x + 8 <= rgb.Width; x += 8)
                {
                    var color = Vector256.LoadUnsafe(ref target, (nuint)(x * 4)).AsUInt32() | Vector256.Create(0xff000000u);
                    var a = Vector256.ShiftRightLogical(Vector256.LoadUnsafe(ref source, (nuint)(x * 4)).AsUInt32(), 24);
                    var repeated = (a | Vector256.ShiftLeft(a, 8) | Vector256.ShiftLeft(a, 16) | Vector256.ShiftLeft(a, 24)).AsByte();
                    var low = Avx2.MultiplyLow(Avx2.UnpackLow(color.AsByte(), Vector256<byte>.Zero).AsUInt16(),
                        Avx2.UnpackLow(repeated, Vector256<byte>.Zero).AsUInt16());
                    var high = Avx2.MultiplyLow(Avx2.UnpackHigh(color.AsByte(), Vector256<byte>.Zero).AsUInt16(),
                        Avx2.UnpackHigh(repeated, Vector256<byte>.Zero).AsUInt16());
                    low = Vector256.ShiftRightLogical(low + Vector256.Create((ushort)1) + Vector256.ShiftRightLogical(low, 8), 8);
                    high = Vector256.ShiftRightLogical(high + Vector256.Create((ushort)1) + Vector256.ShiftRightLogical(high, 8), 8);
                    Avx2.PackUnsignedSaturate(low.AsInt16(), high.AsInt16()).StoreUnsafe(ref target, (nuint)(x * 4));
                }
            }
            if (Sse2.IsSupported)
            {
                for (; x + 4 <= rgb.Width; x += 4)
                {
                    var color = Vector128.LoadUnsafe(ref target, (nuint)(x * 4)).AsUInt32() | Vector128.Create(0xff000000u);
                    var a = Vector128.ShiftRightLogical(Vector128.LoadUnsafe(ref source, (nuint)(x * 4)).AsUInt32(), 24);
                    var repeated = (a | Vector128.ShiftLeft(a, 8) | Vector128.ShiftLeft(a, 16) | Vector128.ShiftLeft(a, 24)).AsByte();
                    var low = Sse2.MultiplyLow(Sse2.UnpackLow(color.AsByte(), Vector128<byte>.Zero).AsUInt16(),
                        Sse2.UnpackLow(repeated, Vector128<byte>.Zero).AsUInt16());
                    var high = Sse2.MultiplyLow(Sse2.UnpackHigh(color.AsByte(), Vector128<byte>.Zero).AsUInt16(),
                        Sse2.UnpackHigh(repeated, Vector128<byte>.Zero).AsUInt16());
                    low = Vector128.ShiftRightLogical(low + Vector128.Create((ushort)1) + Vector128.ShiftRightLogical(low, 8), 8);
                    high = Vector128.ShiftRightLogical(high + Vector128.Create((ushort)1) + Vector128.ShiftRightLogical(high, 8), 8);
                    Sse2.PackUnsignedSaturate(low.AsInt16(), high.AsInt16()).StoreUnsafe(ref target, (nuint)(x * 4));
                }
            }
            for (; x < rgb.Width; x++)
            {
                int p = y * rgb.RowBytes + x * 4;
                byte a = opacity[y * alpha.RowBytes + x * 4 + 3];
                pixels[p] = (byte)(pixels[p] * a / 255);
                pixels[p + 1] = (byte)(pixels[p + 1] * a / 255);
                pixels[p + 2] = (byte)(pixels[p + 2] * a / 255);
                pixels[p + 3] = a;
            }
        }
    }

    private static void WriteRow(ReadOnlySpan<float> red, ReadOnlySpan<float> green, ReadOnlySpan<float> blue, Span<byte> pixels)
    {
        ref float r = ref MemoryMarshal.GetReference(red);
        ref float g = ref MemoryMarshal.GetReference(green);
        ref float b = ref MemoryMarshal.GetReference(blue);
        ref byte target = ref MemoryMarshal.GetReference(pixels);
        int x = 0;
        if (Avx2.IsSupported)
        {
            for (; x + 8 <= red.Length; x += 8)
            {
                var packed = Vector256.Create(0xff000000u) | Quantize(Vector256.LoadUnsafe(ref b, (nuint)x))
                    | Vector256.ShiftLeft(Quantize(Vector256.LoadUnsafe(ref g, (nuint)x)), 8)
                    | Vector256.ShiftLeft(Quantize(Vector256.LoadUnsafe(ref r, (nuint)x)), 16);
                packed.AsByte().StoreUnsafe(ref target, (nuint)(x * 4));
            }
        }
        if (Sse2.IsSupported)
        {
            for (; x + 4 <= red.Length; x += 4)
            {
                var packed = Vector128.Create(0xff000000u) | Quantize(Vector128.LoadUnsafe(ref b, (nuint)x))
                    | Vector128.ShiftLeft(Quantize(Vector128.LoadUnsafe(ref g, (nuint)x)), 8)
                    | Vector128.ShiftLeft(Quantize(Vector128.LoadUnsafe(ref r, (nuint)x)), 16);
                packed.AsByte().StoreUnsafe(ref target, (nuint)(x * 4));
            }
        }
        for (; x < red.Length; x++)
        {
            pixels[x * 4] = (byte)(Math.Clamp(blue[x], 0f, 1f) * 255f);
            pixels[x * 4 + 1] = (byte)(Math.Clamp(green[x], 0f, 1f) * 255f);
            pixels[x * 4 + 2] = (byte)(Math.Clamp(red[x], 0f, 1f) * 255f);
            pixels[x * 4 + 3] = 255;
        }
    }

    // Max/Min 的第二操作数使 NaN 归零；截断保留图片原路径的量化规则。
    private static Vector256<uint> Quantize(Vector256<float> value) => Avx.ConvertToVector256Int32WithTruncation(
        Avx.Min(Avx.Max(value, Vector256<float>.Zero), Vector256.Create(1f)) * Vector256.Create(255f)).AsUInt32();

    private static Vector128<uint> Quantize(Vector128<float> value) => Sse2.ConvertToVector128Int32WithTruncation(
        Sse.Min(Sse.Max(value, Vector128<float>.Zero), Vector128.Create(1f)) * Vector128.Create(255f)).AsUInt32();
}
