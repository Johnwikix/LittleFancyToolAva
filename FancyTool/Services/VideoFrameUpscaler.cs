using SkiaSharp;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FancyToolAva.Services;

/// <summary>Runs a fixed-shape model tile without retaining any previous video frames.</summary>
internal interface IVideoSrTileModel : IDisposable
{
    void Infer(float[] input, float[] output, CancellationToken ct);
}

/// <summary>Bounds accelerator memory with padded, fixed-size tiles and reuses tensor buffers across frames.</summary>
internal sealed class VideoFrameUpscaler(IVideoSrTileModel model, VideoSrDevice device, int window = VideoFrameUpscaler.Window, int nativeScale = 4) : IVideoFrameUpscaler
{
    internal const int Window = 192;
    private const int Pad = 32;
    private readonly int _tile = window - Pad * 2;
    internal const int MaxDimension = 8192;
    internal const long MaxPixels = 33_554_432;
    private readonly float[] _input = new float[3 * window * window];
    private readonly float[] _output = new float[3 * window * window * nativeScale * nativeScale];
    private bool _disposed;
    private TileBuffer[]? _pipelineBuffers;

    private sealed class TileBuffer(float[] input, float[] output)
    {
        internal readonly float[] Input = input;
        internal readonly float[] Output = output;
        internal int X, Y;
    }

    public VideoSrDevice Device => device;

    internal static void ValidateSize(int width, int height, int scale)
    {
        if (scale is not (2 or 4)) throw new ArgumentOutOfRangeException(nameof(scale));
        long outputWidth = (long)width * scale, outputHeight = (long)height * scale;
        if (width <= 0 || height <= 0 || outputWidth > MaxDimension || outputHeight > MaxDimension || outputWidth * outputHeight > MaxPixels)
            throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_SrSizeLimit"));
    }

    public SKBitmap Upscale(SKBitmap source, int scale, CancellationToken ct) => Upscale(source, scale, ct, null);

    internal SKBitmap Upscale(SKBitmap source, int scale, CancellationToken ct, IProgress<double>? progress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateSize(source.Width, source.Height, scale);
        if (scale > nativeScale) throw new ArgumentOutOfRangeException(nameof(scale), "This model supports 2× output only");
        int totalTiles = ((source.Width + _tile - 1) / _tile) * ((source.Height + _tile - 1) / _tile);
        int tileIndex = 0;
        progress?.Report(0.05);
        var result = new SKBitmap(new SKImageInfo(source.Width * scale, source.Height * scale, SKColorType.Bgra8888, SKAlphaType.Opaque));
        try
        {
            // 多块帧才能摊薄工作线程启动成本；小图沿用直接路径。
            if (device.IsHardware && totalTiles >= 8)
            {
                _pipelineBuffers ??=
                [
                    new(_input, _output),
                    new(new float[_input.Length], new float[_output.Length]),
                    new(new float[_input.Length], new float[_output.Length])
                ];
                int nextTile = 0;
                int columns = (source.Width + _tile - 1) / _tile;
                BoundedVideoWorkPipeline.Run(_pipelineBuffers, (buffer, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (nextTile == totalTiles) return false;
                    buffer.X = nextTile % columns * _tile;
                    buffer.Y = nextTile++ / columns * _tile;
                    long stamp = VideoPipelineMetrics.Start();
                    FillInput(source, buffer.X, buffer.Y, window, Pad, buffer.Input);
                    VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrPrepare, stamp);
                    return true;
                }, (buffer, token) =>
                {
                    long stamp = VideoPipelineMetrics.Start();
                    model.Infer(buffer.Input, buffer.Output, token);
                    VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrRun, stamp);
                }, (buffer, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    long stamp = VideoPipelineMetrics.Start();
                    PasteOutput(result, buffer.X, buffer.Y, Math.Min(_tile, source.Width - buffer.X),
                        Math.Min(_tile, source.Height - buffer.Y), scale, window, Pad, nativeScale, buffer.Output);
                    VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrPaste, stamp);
                    progress?.Report(0.05 + 0.8 * ++tileIndex / totalTiles);
                }, ct);
                return result;
            }
            for (int y = 0; y < source.Height; y += _tile)
            for (int x = 0; x < source.Width; x += _tile)
            {
                ct.ThrowIfCancellationRequested();
                long stamp = VideoPipelineMetrics.Start();
                FillInput(source, x, y);
                VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrPrepare, stamp);
                stamp = VideoPipelineMetrics.Start();
                model.Infer(_input, _output, ct);
                VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrRun, stamp);
                stamp = VideoPipelineMetrics.Start();
                PasteOutput(result, x, y, Math.Min(_tile, source.Width - x), Math.Min(_tile, source.Height - y), scale);
                VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrPaste, stamp);
                progress?.Report(0.05 + 0.8 * ++tileIndex / totalTiles);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private void FillInput(SKBitmap source, int x, int y) => FillInput(source, x, y, window, Pad, _input);

    internal static void FillInput(SKBitmap source, int x, int y, int window, int pad, Span<float> input)
    {
        var pixels = source.GetPixelSpan();
        int channel = window * window;
        ref byte pixelsStart = ref MemoryMarshal.GetReference(pixels);
        ref float inputStart = ref MemoryMarshal.GetReference(input);
        for (int row = 0; row < window; row++)
        {
            int sourceRow = Math.Clamp(y + row - pad, 0, source.Height - 1) * source.RowBytes;
            for (int col = 0; col < window;)
            {
                int sourceX = x + col - pad;
                int destination = row * window + col;
                if (Sse2.IsSupported && col + 4 <= window && sourceX >= 0 && sourceX + 4 <= source.Width)
                {
                    var bgra = Vector128.LoadUnsafe(ref pixelsStart, (nuint)(sourceRow + sourceX * 4)).AsUInt32();
                    var mask = Vector128.Create(255u);
                    var red = Sse2.ConvertToVector128Single((Vector128.ShiftRightLogical(bgra, 16) & mask).AsInt32());
                    var green = Sse2.ConvertToVector128Single((Vector128.ShiftRightLogical(bgra, 8) & mask).AsInt32());
                    var blue = Sse2.ConvertToVector128Single((bgra & mask).AsInt32());
                    var divisor = Vector128.Create(255f);
                    (red / divisor).StoreUnsafe(ref inputStart, (nuint)destination);
                    (green / divisor).StoreUnsafe(ref inputStart, (nuint)(channel + destination));
                    (blue / divisor).StoreUnsafe(ref inputStart, (nuint)(channel * 2 + destination));
                    col += 4;
                    continue;
                }
                int index = sourceRow + Math.Clamp(sourceX, 0, source.Width - 1) * 4;
                input[destination] = pixels[index + 2] / 255f;
                input[channel + destination] = pixels[index + 1] / 255f;
                input[channel * 2 + destination] = pixels[index] / 255f;
                col++;
            }
        }
    }

    private void PasteOutput(SKBitmap destination, int x, int y, int width, int height, int scale)
        => PasteOutput(destination, x, y, width, height, scale, window, Pad, nativeScale, _output);

    internal static void PasteOutput(SKBitmap destination, int x, int y, int width, int height, int scale,
        int window, int pad, int nativeScale, ReadOnlySpan<float> output)
    {
        var pixels = destination.GetPixelSpan();
        int outputWindow = window * nativeScale, channel = outputWindow * outputWindow;
        int step = nativeScale / scale;
        for (int row = 0; row < height * scale; row++)
        {
            int col = 0;
            if (Sse41.IsSupported)
            {
                ref float sourceStart = ref MemoryMarshal.GetReference(output);
                ref byte targetStart = ref MemoryMarshal.GetReference(pixels);
                int sourceRow = (pad * nativeScale + row * step) * outputWindow + pad * nativeScale;
                int targetRow = (y * scale + row) * destination.RowBytes + x * scale * 4;
                for (; col + 4 <= width * scale; col += 4)
                {
                    int index = sourceRow + col * step;
                    var red = ReadChannel(ref sourceStart, index, outputWindow, step);
                    var green = ReadChannel(ref sourceStart, index + channel, outputWindow, step);
                    var blue = ReadChannel(ref sourceStart, index + channel * 2, outputWindow, step);
                    var bgra = Vector128.Create(0xff000000u) | blue
                        | Vector128.ShiftLeft(green, 8) | Vector128.ShiftLeft(red, 16);
                    bgra.AsByte().StoreUnsafe(ref targetStart, (nuint)(targetRow + col * 4));
                }
            }
            for (; col < width * scale; col++)
            {
                int source = (pad * nativeScale + row * step) * outputWindow + pad * nativeScale + col * step;
                int target = (y * scale + row) * destination.RowBytes + (x * scale + col) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float value = output[c * channel + source];
                    if (step == 2)
                        value = (value + output[c * channel + source + 1] + output[c * channel + source + outputWindow]
                            + output[c * channel + source + outputWindow + 1]) * 0.25f;
                    pixels[target + 2 - c] = (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
                }
                pixels[target + 3] = 255;
            }
        }
    }

    // Four BGRA pixels at once; scalar code above remains the portable fallback.
    private static Vector128<uint> ReadChannel(ref float data, int index, int stride, int step)
    {
        var value = Vector128.LoadUnsafe(ref data, (nuint)index);
        if (step == 2)
        {
            // Match the scalar summation order: (a + b + c + d) / 4.
            var a = Sse.Shuffle(value, Vector128.LoadUnsafe(ref data, (nuint)(index + 4)), 0x88);
            var b = Sse.Shuffle(value, Vector128.LoadUnsafe(ref data, (nuint)(index + 4)), 0xdd);
            var lower = Vector128.LoadUnsafe(ref data, (nuint)(index + stride));
            var lowerNext = Vector128.LoadUnsafe(ref data, (nuint)(index + stride + 4));
            var c = Sse.Shuffle(lower, lowerNext, 0x88);
            var d = Sse.Shuffle(lower, lowerNext, 0xdd);
            value = ((a + b) + c + d) * Vector128.Create(.25f);
        }
        value = Sse41.RoundToNearestInteger(Sse.Min(Sse.Max(value * Vector128.Create(255f), Vector128<float>.Zero), Vector128.Create(255f)));
        return Sse2.ConvertToVector128Int32(value).AsUInt32();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pipelineBuffers = null;
        model.Dispose();
    }
}
