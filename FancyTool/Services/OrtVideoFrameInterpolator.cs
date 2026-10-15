using Microsoft.ML.OnnxRuntime;
using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>Runs the upstream seven-channel RIFE v2 graph with a reusable frame-pair input tensor.</summary>
internal sealed class OrtVideoFrameInterpolator : IVideoFrameInterpolator
{
    private readonly InferenceSession _session;
    private readonly float[] _input;
    private readonly OrtValue _tensor;
    private readonly Dictionary<string, OrtValue> _inputs;
    private readonly string[] _outputs;
    private readonly int _width;
    private readonly int _height;
    private readonly int _inputWidth;
    private readonly int _inputHeight;
    private bool _disposed;

    /// <inheritdoc />
    public VideoSrDevice Device { get; }

    internal OrtVideoFrameInterpolator(string path, VideoSrBackend backend, int width, int height,
        CancellationToken ct, string? fallbackReason = null)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 3840L * 2160)
            throw new ArgumentOutOfRangeException(nameof(width), LocalizationRegistry.Get("VideoTranscode.Interp_SizeLimit"));
        _width = width;
        _height = height;
        // Upstream reflects to a multiple of 128. A <=64-pixel axis would
        // require a reflect pad at least as wide as the original picture.
        // Extend tiny frames at the edge before inference and crop the result.
        _inputWidth = width <= 64 ? 128 : width;
        _inputHeight = height <= 64 ? 128 : height;
        if ((long)_inputWidth * _inputHeight > 3840L * 2160)
            throw new ArgumentOutOfRangeException(nameof(width), LocalizationRegistry.Get("VideoTranscode.Interp_SizeLimit"));
        _session = WindowsMlAccelerator.CreateSession(path, backend, _inputWidth, _inputHeight, ct, out var device, requireHardware: true);
        Device = device with { FallbackReason = fallbackReason ?? device.FallbackReason };
        try
        {
            var metadata = _session.InputMetadata.Single();
            if (metadata.Value.ElementDataType != Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Float
                || metadata.Value.Dimensions.Length != 4 || metadata.Value.Dimensions[1] != 7)
                throw new NotSupportedException(LocalizationRegistry.Get("VideoTranscode.Interp_InvalidModel"));
            _input = new float[checked(7 * _inputWidth * _inputHeight)];
            _tensor = OrtValue.CreateTensorValueFromMemory(_input, [1, 7, _inputHeight, _inputWidth]);
            _inputs = new Dictionary<string, OrtValue> { [metadata.Key] = _tensor };
            _outputs = [_session.OutputMetadata.Single().Key];
        }
        catch { _session.Dispose(); throw; }
    }

    /// <inheritdoc />
    public unsafe void SetFrames(SKBitmap first, SKBitmap second, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CopyFrame(first, 0, ct);
        CopyFrame(second, 3 * _inputWidth * _inputHeight, ct);
    }

    private unsafe void CopyFrame(SKBitmap bitmap, int start, CancellationToken ct)
    {
        int plane = _inputWidth * _inputHeight;
        if (bitmap.Width != _width || bitmap.Height != _height || bitmap.ColorType != SKColorType.Bgra8888)
            throw new ArgumentException(LocalizationRegistry.Get("VideoTranscode.Interp_InvalidFrames"));
        byte* pixels = (byte*)bitmap.GetPixels();
        for (int y = 0; y < _inputHeight; y++)
        {
            ct.ThrowIfCancellationRequested();
            byte* row = pixels + Math.Min(y, _height - 1) * bitmap.RowBytes;
            for (int x = 0; x < _inputWidth; x++)
            {
                int index = start + y * _inputWidth + x;
                int pixel = Math.Min(x, _width - 1) * 4;
                _input[index] = row[pixel + 2] / 255f;
                _input[index + plane] = row[pixel + 1] / 255f;
                _input[index + 2 * plane] = row[pixel] / 255f;
            }
        }
    }

    /// <inheritdoc />
    public unsafe void Interpolate(double amount, SKBitmap destination, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(amount) || amount <= 0 || amount >= 1) throw new ArgumentOutOfRangeException(nameof(amount));
        if (destination.Width != _width || destination.Height != _height || destination.ColorType != SKColorType.Bgra8888)
            throw new ArgumentException(LocalizationRegistry.Get("VideoTranscode.Interp_InvalidFrames"));
        ct.ThrowIfCancellationRequested();
        int plane = _inputWidth * _inputHeight;
        _input.AsSpan(6 * plane).Fill((float)amount);
        using var run = new RunOptions();
        using var registration = ct.Register(() => run.Terminate = true);
        try
        {
            using var results = _session.Run(run, _inputs, _outputs);
            var output = results[0];
            var shape = output.GetTensorTypeAndShape().Shape;
            if (shape.Length != 4 || shape[0] != 1 || shape[1] != 3 || shape[2] != _inputHeight || shape[3] != _inputWidth)
                throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Interp_InvalidModel"));
            var values = output.GetTensorDataAsSpan<float>();
            byte* target = (byte*)destination.GetPixels();
            for (int y = 0; y < _height; y++)
            {
                ct.ThrowIfCancellationRequested();
                byte* row = target + y * destination.RowBytes;
                for (int x = 0; x < _width; x++)
                {
                    int i = y * _inputWidth + x;
                    row[x * 4] = ToByte(values[2 * plane + i]);
                    row[x * 4 + 1] = ToByte(values[plane + i]);
                    row[x * 4 + 2] = ToByte(values[i]);
                    row[x * 4 + 3] = 255;
                }
            }
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    private static byte ToByte(float value) => (byte)Math.Clamp(float.IsFinite(value) ? value * 255f + 0.5f : 0, 0, 255);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tensor.Dispose();
        _session.Dispose();
    }
}
