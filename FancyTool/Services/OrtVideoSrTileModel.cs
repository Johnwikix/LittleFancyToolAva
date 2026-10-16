using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Numerics.Tensors;

namespace FancyToolAva.Services;

/// <summary>Runs RGB FP16/FP32 image or video graphs with cancelable, serial inference.</summary>
internal sealed class OrtVideoSrTileModel : IVideoSrTileModel
{
    private readonly InferenceSession _session;
    private readonly Dictionary<string, OrtValue> _inputs = new();
    private readonly List<GCHandle> _pins = [];
    private readonly string[] _outputNames;
    private readonly float[]? _floatInput;
    private readonly Half[]? _halfInput;
    private readonly int _window;
    private readonly int _nativeScale;
    internal VideoSrDevice Device { get; }

    internal OrtVideoSrTileModel(string path, VideoSrBackend backend, int window, CancellationToken ct, int nativeScale = 4,
        bool requireHardware = false, bool? useFp16 = null)
    {
        _window = window;
        _nativeScale = nativeScale;
        _session = WindowsMlAccelerator.CreateSession(path, backend, window, window, ct, out var device, requireHardware, useFp16);
        Device = device;
        try
        {
            var imageInput = _session.InputMetadata.First(kv => kv.Value.Dimensions.Length == 4);
            var shape = new long[] { 1, 3, window, window };
            if (imageInput.Value.ElementDataType == TensorElementType.Float16)
            {
                _halfInput = new Half[3 * window * window];
                _inputs.Add(imageInput.Key, MakeHalf(_halfInput, shape));
            }
            else if (imageInput.Value.ElementDataType == TensorElementType.Float)
            {
                _floatInput = new float[3 * window * window];
                _inputs.Add(imageInput.Key, OrtValue.CreateTensorValueFromMemory(_floatInput, shape));
            }
            else throw new NotSupportedException("Unsupported model input precision");
            foreach (var extra in _session.InputMetadata.Where(kv => kv.Key != imageInput.Key))
            {
                if (extra.Key != "denoise_strength") throw new NotSupportedException($"Unsupported model input: {extra.Key}");
                _inputs.Add(extra.Key, extra.Value.ElementDataType == TensorElementType.Float16
                    ? MakeHalf([(Half)0.5f], [1]) : OrtValue.CreateTensorValueFromMemory(new float[] { 0.5f }, [1]));
            }
            _outputNames = [_session.OutputMetadata.First().Key];
        }
        catch { Dispose(); throw; }
    }

    private OrtValue MakeHalf(Half[] data, long[] shape)
    {
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        _pins.Add(pin);
        return OrtValue.CreateTensorValueWithData(OrtMemoryInfo.DefaultInstance, TensorElementType.Float16, shape,
            pin.AddrOfPinnedObject(), data.Length * 2L);
    }

    public void Infer(float[] input, float[] output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_halfInput != null) TensorPrimitives.ConvertToHalf(input, _halfInput);
        else input.CopyTo(_floatInput!, 0);
        using var options = new RunOptions();
        using var registration = ct.Register(() => options.Terminate = true);
        try
        {
            using var results = _session.Run(options, _inputs, _outputNames);
            var value = results[0];
            var shape = value.GetTensorTypeAndShape();
            if (!shape.Shape.SequenceEqual(new long[] { 1, 3, _window * _nativeScale, _window * _nativeScale }))
                throw new InvalidOperationException("Unexpected super-resolution output shape");
            if (shape.ElementDataType == TensorElementType.Float16)
            {
                TensorPrimitives.ConvertToSingle(MemoryMarshal.Cast<Float16, Half>(value.GetTensorDataAsSpan<Float16>()), output);
            }
            else value.GetTensorDataAsSpan<float>().CopyTo(output);
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        ct.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        foreach (var input in _inputs.Values) input.Dispose();
        _inputs.Clear();
        foreach (var pin in _pins) pin.Free();
        _pins.Clear();
        _session.Dispose();
    }
}
