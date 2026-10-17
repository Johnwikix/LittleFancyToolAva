using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>CPU 只复制 BGRA 字节及边缘，不展开浮点通道。</summary>
internal static class VideoBgraPixels
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void CopyPadded(SKBitmap source, int x, int y, int width, int height, int pad,
        Span<byte> target, CancellationToken ct)
    {
        var pixels = source.GetPixelSpan();
        int left = Math.Clamp(pad - x, 0, width);
        int sourceX = Math.Clamp(x - pad, 0, source.Width - 1);
        int count = Math.Min(width - left, source.Width - sourceX);
        for (int row = 0; row < height; row++)
        {
            ct.ThrowIfCancellationRequested();
            int sourceRow = Math.Clamp(y + row - pad, 0, source.Height - 1) * source.RowBytes;
            var destination = target.Slice(row * width * 4, width * 4);
            pixels.Slice(sourceRow + sourceX * 4, count * 4).CopyTo(destination[(left * 4)..]);
            var words = MemoryMarshal.Cast<byte, uint>(destination);
            if (left > 0) words[..left].Fill(MemoryMarshal.Read<uint>(pixels.Slice(sourceRow, 4)));
            if (left + count < width) words[(left + count)..].Fill(words[left + count - 1]);
        }
    }

    internal static bool Matches(SKBitmap source, ReadOnlySpan<byte> previous, int previousStride, CancellationToken ct)
        => EqualRows(source.GetPixelSpan(), source.RowBytes, previous, previousStride, source.Width, source.Height, ct);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool EqualRows(ReadOnlySpan<byte> current, int currentStride, ReadOnlySpan<byte> previous,
        int previousStride, int width, int height, CancellationToken ct)
    {
        // 采样快速排除变化帧；只有完整逐行相等才允许复用，采样不会决定命中。
        for (int y = 0; y < height; y += Math.Max(1, height / 8))
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x += Math.Max(1, width / 8))
                if (!current.Slice(y * currentStride + x * 4, 4).SequenceEqual(previous.Slice(y * previousStride + x * 4, 4))) return false;
        }
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            if (!current.Slice(y * currentStride, width * 4).SequenceEqual(previous.Slice(y * previousStride, width * 4))) return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Paste(ReadOnlySpan<byte> source, int stride, SKBitmap destination,
        int x, int y, int width, int height, CancellationToken ct)
    {
        var pixels = destination.GetPixelSpan();
        if (x == 0 && y == 0 && width == destination.Width && height == destination.Height && stride == destination.RowBytes)
        {
            ct.ThrowIfCancellationRequested();
            source[..pixels.Length].CopyTo(pixels);
            return;
        }
        for (int row = 0; row < height; row++)
        {
            ct.ThrowIfCancellationRequested();
            source.Slice(row * stride, width * 4).CopyTo(pixels.Slice((y + row) * destination.RowBytes + x * 4, width * 4));
        }
    }

}
