using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>Runs fused NanoVSR on real adjacent frames with bounded spatial and temporal memory.</summary>
internal sealed class OrtNanoVsrUpscaler : IVideoFrameSequenceUpscaler
{
    internal const int Frames = 5;
    private const int Pad = 32;
    private readonly InferenceSession _session;
    private readonly int _window;
    private readonly float[] _tileInput;
    private readonly float[] _tileOutput;
    private readonly Float16[]? _halfInput;
    private readonly float[]? _floatInput;
    private readonly OrtValue _input = null!;
    private readonly Dictionary<string, OrtValue> _inputs;
    private readonly string[] _outputNames;
    private readonly long[] _outputShape;
    private bool _disposed;

    public VideoSrDevice Device { get; }
    public int TemporalWindow => Frames;

    internal OrtNanoVsrUpscaler(string path, VideoSrBackend backend, int window, CancellationToken ct,
        string? fallbackReason = null)
    {
        if (window <= 2 * Pad) throw new ArgumentOutOfRangeException(nameof(window));
        _window = window;
        _session = WindowsMlAccelerator.CreateTensorSession(path, backend,
            new Dictionary<string, long> { ["batch"] = 1, ["height"] = window, ["width"] = window }, ct, out var device,
            requireHardware: backend != VideoSrBackend.Cpu);
        Device = device with { FallbackReason = fallbackReason ?? device.FallbackReason };
        try
        {
            var metadata = _session.InputMetadata.Single();
            if (metadata.Value.Dimensions.Length != 5 || metadata.Value.Dimensions[1] != Frames)
                throw new InvalidOperationException("NanoVSR requires a five-frame [N,T,C,H,W] model");
            long[] shape = [1, Frames, 3, window, window];
            int length = Frames * 3 * window * window;
            if (metadata.Value.ElementDataType == TensorElementType.Float16)
            {
                _halfInput = new Float16[length];
                _input = OrtValue.CreateTensorValueFromMemory(_halfInput, shape);
            }
            else if (metadata.Value.ElementDataType == TensorElementType.Float)
            {
                _floatInput = new float[length];
                _input = OrtValue.CreateTensorValueFromMemory(_floatInput, shape);
            }
            else throw new NotSupportedException("Unsupported NanoVSR tensor precision");
            _inputs = new Dictionary<string, OrtValue> { [metadata.Key] = _input };
            _outputNames = [_session.OutputMetadata.Single().Key];
            _outputShape = [1, Frames, 3, window * 4, window * 4];
            _tileInput = new float[3 * window * window];
            _tileOutput = new float[3 * window * window * 16];
        }
        catch { _input?.Dispose(); _session.Dispose(); throw; }
    }

    public SKBitmap Upscale(SKBitmap source, int scale, CancellationToken ct) => UpscaleSequence([source], scale, ct)[0];

    public SKBitmap[] UpscaleSequence(IReadOnlyList<SKBitmap> source, int scale, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (source.Count is < 1 or > Frames) throw new ArgumentOutOfRangeException(nameof(source));
        int width = source[0].Width, height = source[0].Height;
        VideoFrameUpscaler.ValidateSize(width, height, scale);
        if (source.Any(frame => frame.Width != width || frame.Height != height))
            throw new ArgumentException("All temporal frames must have the same dimensions", nameof(source));
        var result = new SKBitmap[source.Count];
        try
        {
            for (int f = 0; f < result.Length; f++)
                result[f] = new SKBitmap(new SKImageInfo(width * scale, height * scale, SKColorType.Bgra8888, SKAlphaType.Opaque));
            int tile = _window - 2 * Pad;
            for (int y = 0; y < height; y += tile)
            for (int x = 0; x < width; x += tile)
            {
                ct.ThrowIfCancellationRequested();
                for (int f = 0; f < Frames; f++)
                {
                    // Only an incomplete final window repeats its last frame. Full windows use real neighbours.
                    VideoFrameUpscaler.FillInput(source[Math.Min(f, source.Count - 1)], x, y, _window, Pad, _tileInput);
                    int offset = f * _tileInput.Length;
                    if (_halfInput != null)
                        TensorPrimitives.ConvertToHalf(_tileInput, MemoryMarshal.Cast<Float16, Half>(_halfInput.AsSpan(offset, _tileInput.Length)));
                    else _tileInput.CopyTo(_floatInput!, offset);
                }
                using var options = new RunOptions();
                using var cancellation = ct.Register(() => options.Terminate = true);
                try
                {
                    using var outputs = _session.Run(options, _inputs, _outputNames);
                    var output = outputs[0];
                    var info = output.GetTensorTypeAndShape();
                    if (!info.Shape.AsSpan().SequenceEqual(_outputShape)) throw new InvalidOperationException("Unexpected NanoVSR output shape");
                    for (int f = 0; f < result.Length; f++)
                    {
                        ct.ThrowIfCancellationRequested();
                        int offset = f * _tileOutput.Length;
                        if (info.ElementDataType == TensorElementType.Float16)
                            TensorPrimitives.ConvertToSingle(MemoryMarshal.Cast<Float16, Half>(output.GetTensorDataAsSpan<Float16>()
                                .Slice(offset, _tileOutput.Length)), _tileOutput);
                        else output.GetTensorDataAsSpan<float>().Slice(offset, _tileOutput.Length).CopyTo(_tileOutput);
                        VideoFrameUpscaler.PasteOutput(result[f], x, y, Math.Min(tile, width - x), Math.Min(tile, height - y),
                            scale, _window, Pad, 4, _tileOutput);
                    }
                }
                catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            }
            return result;
        }
        catch { foreach (var frame in result) frame?.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _input.Dispose();
        _session.Dispose();
    }
}
