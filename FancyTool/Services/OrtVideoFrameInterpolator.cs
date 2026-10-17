using Microsoft.ML.OnnxRuntime;
using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>Runs the upstream seven-channel RIFE v2 graph with a reusable frame-pair input tensor.</summary>
internal sealed class OrtVideoFrameInterpolator : IVideoFrameInterpolator
{
    private readonly InferenceSession _session;
    private readonly float[] _input;
    private readonly OrtValue _tensor;
    private readonly string[] _outputs;
    private readonly string[] _inputNames;
    private readonly OrtValue[] _inputValues;
    private readonly float[] _outputData;
    private readonly OrtValue _outputTensor;
    private readonly OrtValue[] _outputValues;
    private readonly int _width;
    private readonly int _height;
    private readonly int _inputWidth;
    private readonly int _inputHeight;
    private bool _disposed;
    private readonly VideoInferenceRunGuard _runGuard;

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
        _runGuard = new(device);
        try
        {
            var metadata = _session.InputMetadata.Single();
            if (metadata.Value.ElementDataType != Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Float
                || metadata.Value.Dimensions.Length != 4 || metadata.Value.Dimensions[1] != 7)
                throw new NotSupportedException(LocalizationRegistry.Get("VideoTranscode.Interp_InvalidModel"));
            _input = new float[checked(7 * _inputWidth * _inputHeight)];
            _tensor = OrtValue.CreateTensorValueFromMemory(_input, [1, 7, _inputHeight, _inputWidth]);
            _outputs = [_session.OutputMetadata.Single().Key];
            _inputNames = [metadata.Key];
            _inputValues = [_tensor];
            _outputData = new float[checked(3 * _inputWidth * _inputHeight)];
            _outputTensor = OrtValue.CreateTensorValueFromMemory(_outputData, [1, 3, _inputHeight, _inputWidth]);
            _outputValues = [_outputTensor];
        }
        catch
        {
            _outputTensor?.Dispose();
            _tensor?.Dispose();
            _session.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void SetFrames(SKBitmap first, SKBitmap second, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long stamp = VideoPipelineMetrics.Start();
        CopyFrame(first, 0, ct);
        CopyFrame(second, 3 * _inputWidth * _inputHeight, ct);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.RifePrepare, stamp);
    }

    private void CopyFrame(SKBitmap bitmap, int start, CancellationToken ct)
    {
        int plane = _inputWidth * _inputHeight;
        if (bitmap.Width != _width || bitmap.Height != _height || bitmap.ColorType != SKColorType.Bgra8888)
            throw new ArgumentException(LocalizationRegistry.Get("VideoTranscode.Interp_InvalidFrames"));
        VideoTensorPixels.Pack(bitmap, _inputWidth, _inputHeight, _input.AsSpan(start, 3 * plane), ct);
    }

    /// <inheritdoc />
    public void Interpolate(double amount, SKBitmap destination, CancellationToken ct)
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
            long stamp = VideoPipelineMetrics.Start();
            _runGuard.Run(_session, run, _inputNames, _inputValues, _outputs, _outputValues, ct);
            VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.RifeRun, stamp);
            stamp = VideoPipelineMetrics.Start();
            VideoTensorPixels.Unpack(_outputData, _inputWidth, _inputHeight, destination, ct);
            VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.RifeReadback, stamp);
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tensor.Dispose();
        _outputTensor.Dispose();
        _session.Dispose();
    }
}
