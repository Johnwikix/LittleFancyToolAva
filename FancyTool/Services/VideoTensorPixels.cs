using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>RIFE 的 BGRA/平面 RGB 转换；保留边缘扩展、非有限值和舍入规则。</summary>
internal static class VideoTensorPixels
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Pack(SKBitmap bitmap, int width, int height, Span<float> destination, CancellationToken ct)
    {
        int plane = width * height;
        ref byte pixels = ref MemoryMarshal.GetReference(bitmap.GetPixelSpan());
        ref float target = ref MemoryMarshal.GetReference(destination);
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            int row = Math.Min(y, bitmap.Height - 1) * bitmap.RowBytes;
            int x = 0;
            if (Avx2.IsSupported)
            {
                for (; x + 8 <= Math.Min(width, bitmap.Width); x += 8)
                {
                    var bgra = Vector256.LoadUnsafe(ref pixels, (nuint)(row + x * 4)).AsUInt32();
                    var mask = Vector256.Create(255u);
                    var divisor = Vector256.Create(255f);
                    int offset = y * width + x;
                    (Avx.ConvertToVector256Single((Vector256.ShiftRightLogical(bgra, 16) & mask).AsInt32()) / divisor)
                        .StoreUnsafe(ref target, (nuint)offset);
                    (Avx.ConvertToVector256Single((Vector256.ShiftRightLogical(bgra, 8) & mask).AsInt32()) / divisor)
                        .StoreUnsafe(ref target, (nuint)(plane + offset));
                    (Avx.ConvertToVector256Single((bgra & mask).AsInt32()) / divisor)
                        .StoreUnsafe(ref target, (nuint)(2 * plane + offset));
                }
            }
            if (Sse2.IsSupported)
            {
                for (; x + 4 <= Math.Min(width, bitmap.Width); x += 4)
                {
                    var bgra = Vector128.LoadUnsafe(ref pixels, (nuint)(row + x * 4)).AsUInt32();
                    var mask = Vector128.Create(255u);
                    var divisor = Vector128.Create(255f);
                    int offset = y * width + x;
                    (Sse2.ConvertToVector128Single((Vector128.ShiftRightLogical(bgra, 16) & mask).AsInt32()) / divisor)
                        .StoreUnsafe(ref target, (nuint)offset);
                    (Sse2.ConvertToVector128Single((Vector128.ShiftRightLogical(bgra, 8) & mask).AsInt32()) / divisor)
                        .StoreUnsafe(ref target, (nuint)(plane + offset));
                    (Sse2.ConvertToVector128Single((bgra & mask).AsInt32()) / divisor)
                        .StoreUnsafe(ref target, (nuint)(2 * plane + offset));
                }
            }
            for (; x < width; x++)
            {
                int offset = y * width + x, pixel = row + Math.Min(x, bitmap.Width - 1) * 4;
                destination[offset] = System.Runtime.CompilerServices.Unsafe.Add(ref pixels, pixel + 2) / 255f;
                destination[plane + offset] = System.Runtime.CompilerServices.Unsafe.Add(ref pixels, pixel + 1) / 255f;
                destination[2 * plane + offset] = System.Runtime.CompilerServices.Unsafe.Add(ref pixels, pixel) / 255f;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Unpack(ReadOnlySpan<float> source, int width, int height, SKBitmap bitmap, CancellationToken ct)
    {
        int plane = width * height;
        var pixels = bitmap.GetPixelSpan();
        ref byte target = ref MemoryMarshal.GetReference(pixels);
        ref float values = ref MemoryMarshal.GetReference(source);
        for (int y = 0; y < bitmap.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            int x = 0;
            if (Avx2.IsSupported)
            {
                for (; x + 8 <= bitmap.Width; x += 8)
                {
                    int offset = y * width + x;
                    var red = Quantize(Vector256.LoadUnsafe(ref values, (nuint)offset));
                    var green = Quantize(Vector256.LoadUnsafe(ref values, (nuint)(plane + offset)));
                    var blue = Quantize(Vector256.LoadUnsafe(ref values, (nuint)(2 * plane + offset)));
                    var bgra = Vector256.Create(0xff000000u) | blue | Vector256.ShiftLeft(green, 8) | Vector256.ShiftLeft(red, 16);
                    bgra.AsByte().StoreUnsafe(ref target, (nuint)(y * bitmap.RowBytes + x * 4));
                }
            }
            if (Sse2.IsSupported)
            {
                for (; x + 4 <= bitmap.Width; x += 4)
                {
                    int offset = y * width + x;
                    var red = Quantize(Vector128.LoadUnsafe(ref values, (nuint)offset));
                    var green = Quantize(Vector128.LoadUnsafe(ref values, (nuint)(plane + offset)));
                    var blue = Quantize(Vector128.LoadUnsafe(ref values, (nuint)(2 * plane + offset)));
                    var bgra = Vector128.Create(0xff000000u) | blue | Vector128.ShiftLeft(green, 8) | Vector128.ShiftLeft(red, 16);
                    bgra.AsByte().StoreUnsafe(ref target, (nuint)(y * bitmap.RowBytes + x * 4));
                }
            }
            for (; x < bitmap.Width; x++)
            {
                int offset = y * width + x, pixel = y * bitmap.RowBytes + x * 4;
                pixels[pixel] = Quantize(source[2 * plane + offset]);
                pixels[pixel + 1] = Quantize(source[plane + offset]);
                pixels[pixel + 2] = Quantize(source[offset]);
                pixels[pixel + 3] = 255;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Quantize(Vector256<float> value)
    {
        var finite = Vector256.LessThanOrEqual(Vector256.Abs(value), Vector256.Create(float.MaxValue));
        value = (value & finite) * Vector256.Create(255f) + Vector256.Create(.5f);
        value = Avx.Min(Avx.Max(value, Vector256<float>.Zero), Vector256.Create(255f));
        return Avx.ConvertToVector256Int32WithTruncation(value).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Quantize(Vector128<float> value)
    {
        // NaN/±Infinity 与原标量实现一致地归零；使用截断实现 value * 255 + 0.5。
        var finite = Sse.CompareLessThanOrEqual(Sse.AndNot(Vector128.Create(-0f), value), Vector128.Create(float.MaxValue));
        value = Sse.And(value, finite) * Vector128.Create(255f) + Vector128.Create(.5f);
        value = Sse.Min(Sse.Max(value, Vector128<float>.Zero), Vector128.Create(255f));
        return Sse2.ConvertToVector128Int32WithTruncation(value).AsUInt32();
    }

    private static byte Quantize(float value) => (byte)Math.Clamp(float.IsFinite(value) ? value * 255f + .5f : 0, 0, 255);
}
