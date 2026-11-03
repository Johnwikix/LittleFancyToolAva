using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Numerics.Tensors;

namespace FancyToolAva.Services;

/// <summary>复用固定形状的输入输出张量，并保持可取消的串行推理。</summary>
internal sealed class OrtVideoSrTileModel : IVideoSrTileModel
{
    private readonly InferenceSession _session;
    private readonly Dictionary<string, OrtValue> _inputs = new();
    private readonly List<GCHandle> _pins = [];
    private readonly string[] _outputNames;
    private readonly string[] _inputNames;
    private readonly OrtValue[] _inputValues;
    private readonly OrtValue[] _outputValues = [];
    private readonly float[]? _floatInput;
    private readonly Half[]? _halfInput;
    private readonly float[]? _floatOutput;
    private readonly Half[]? _halfOutput;
    private bool _disposed;
    private readonly VideoInferenceRunGuard _runGuard;
    internal VideoSrDevice Device { get; }

    internal OrtVideoSrTileModel(string path, VideoSrBackend backend, int window, CancellationToken ct, int nativeScale = 4,
        bool requireHardware = false, bool? useFp16 = null)
    {
        _session = WindowsMlAccelerator.CreateSession(path, backend, window, window, ct, out var device, requireHardware, useFp16);
        Device = device;
        _runGuard = new(device);
        try
        {
            var imageInput = _session.InputMetadata.First(kv => kv.Value.Dimensions.Length == 4);
            long[] shape = [1, 3, window, window];
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
            _inputNames = _inputs.Keys.ToArray();
            _inputValues = _inputs.Values.ToArray();
            long[] outputShape = [1, 3, window * nativeScale, window * nativeScale];
            int outputLength = checked(3 * window * window * nativeScale * nativeScale);
            if (_session.OutputMetadata.First().Value.ElementDataType == TensorElementType.Float16)
            {
                _halfOutput = new Half[outputLength];
                _outputValues = [MakeHalf(_halfOutput, outputShape)];
            }
            else if (_session.OutputMetadata.First().Value.ElementDataType == TensorElementType.Float)
            {
                _floatOutput = new float[outputLength];
                _outputValues = [OrtValue.CreateTensorValueFromMemory(_floatOutput, outputShape)];
            }
            else throw new NotSupportedException("Unsupported model output precision");
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        long stamp = VideoPipelineMetrics.Start();
        if (_halfInput != null) TensorPrimitives.ConvertToHalf(input, _halfInput);
        else input.CopyTo(_floatInput!, 0);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrTensorPrepare, stamp);
        using var options = new RunOptions();
        using var registration = ct.Register(() => options.Terminate = true);
        try
        {
            stamp = VideoPipelineMetrics.Start();
            _runGuard.Run(_session, options, _inputNames, _inputValues, _outputNames, _outputValues, ct);
            VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrNativeRun, stamp);
            stamp = VideoPipelineMetrics.Start();
            if (_halfOutput != null) TensorPrimitives.ConvertToSingle(_halfOutput, output);
            else _floatOutput!.CopyTo(output, 0);
            VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrTensorRead, stamp);
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        ct.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var input in _inputs.Values) input.Dispose();
        foreach (var output in _outputValues) output.Dispose();
        foreach (var pin in _pins) pin.Free();
        _inputs.Clear();
        _pins.Clear();
        _session.Dispose();
    }
}
