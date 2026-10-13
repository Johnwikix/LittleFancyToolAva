using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>Runs a fixed-shape model tile without retaining any previous video frames.</summary>
internal interface IVideoSrTileModel : IDisposable
{
    void Infer(float[] input, float[] output, CancellationToken ct);
}

/// <summary>Bounds accelerator memory with padded, fixed-size tiles and reuses tensor buffers across frames.</summary>
internal sealed class VideoFrameUpscaler(IVideoSrTileModel model, VideoSrDevice device) : IVideoFrameUpscaler
{
    internal const int Window = 192;
    private const int Pad = 32;
    private const int Tile = Window - Pad * 2;
    internal const int MaxDimension = 8192;
    internal const long MaxPixels = 33_554_432;
    private readonly float[] _input = new float[3 * Window * Window];
    private readonly float[] _output = new float[3 * Window * Window * 16];
    private bool _disposed;

    public VideoSrDevice Device => device;

    internal static void ValidateSize(int width, int height, int scale)
    {
        if (scale is not (2 or 4)) throw new ArgumentOutOfRangeException(nameof(scale));
        long outputWidth = (long)width * scale, outputHeight = (long)height * scale;
        if (width <= 0 || height <= 0 || outputWidth > MaxDimension || outputHeight > MaxDimension || outputWidth * outputHeight > MaxPixels)
            throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_SrSizeLimit"));
    }

    public SKBitmap Upscale(SKBitmap source, int scale, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateSize(source.Width, source.Height, scale);
        var result = new SKBitmap(new SKImageInfo(source.Width * scale, source.Height * scale, SKColorType.Bgra8888, SKAlphaType.Opaque));
        try
        {
            for (int y = 0; y < source.Height; y += Tile)
            for (int x = 0; x < source.Width; x += Tile)
            {
                ct.ThrowIfCancellationRequested();
                FillInput(source, x, y);
                model.Infer(_input, _output, ct);
                PasteOutput(result, x, y, Math.Min(Tile, source.Width - x), Math.Min(Tile, source.Height - y), scale);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private void FillInput(SKBitmap source, int x, int y)
    {
        var pixels = source.GetPixelSpan();
        int channel = Window * Window;
        for (int row = 0; row < Window; row++)
        {
            int sourceRow = Math.Clamp(y + row - Pad, 0, source.Height - 1) * source.RowBytes;
            for (int col = 0; col < Window; col++)
            {
                int index = sourceRow + Math.Clamp(x + col - Pad, 0, source.Width - 1) * 4;
                int destination = row * Window + col;
                _input[destination] = pixels[index + 2] / 255f;
                _input[channel + destination] = pixels[index + 1] / 255f;
                _input[channel * 2 + destination] = pixels[index] / 255f;
            }
        }
    }

    private void PasteOutput(SKBitmap destination, int x, int y, int width, int height, int scale)
    {
        var pixels = destination.GetPixelSpan();
        int outputWindow = Window * 4, channel = outputWindow * outputWindow;
        int step = 4 / scale;
        for (int row = 0; row < height * scale; row++)
        for (int col = 0; col < width * scale; col++)
        {
            int source = (Pad * 4 + row * step) * outputWindow + Pad * 4 + col * step;
            int target = (y * scale + row) * destination.RowBytes + (x * scale + col) * 4;
            for (int c = 0; c < 3; c++)
            {
                float value = _output[c * channel + source];
                if (step == 2)
                    value = (value + _output[c * channel + source + 1] + _output[c * channel + source + outputWindow]
                        + _output[c * channel + source + outputWindow + 1]) * 0.25f;
                pixels[target + 2 - c] = (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
            }
            pixels[target + 3] = 255;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        model.Dispose();
    }
}
