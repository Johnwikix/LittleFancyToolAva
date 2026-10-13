using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FancyToolAva.Services;

/// <summary>Runs the shared FP16/FP32 image models on DirectML or CPU with cancelable, serial inference.</summary>
internal sealed class OrtVideoSrTileModel : IVideoSrTileModel
{
    private readonly InferenceSession _session;
    private readonly Dictionary<string, OrtValue> _inputs = new();
    private readonly List<GCHandle> _pins = [];
    private readonly string[] _outputNames;
    private readonly float[]? _floatInput;
    private readonly Half[]? _halfInput;

    public OrtVideoSrTileModel(string path, int? gpuIndex)
    {
        using var options = new SessionOptions
        {
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableCpuMemArena = false,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        if (gpuIndex.HasValue) options.AppendExecutionProvider_DML(gpuIndex.Value);
        else options.AppendExecutionProvider_CPU(0);
        _session = new InferenceSession(path, options);
        try
        {
            var imageInput = _session.InputMetadata.First(kv => kv.Value.Dimensions.Length == 4);
            var shape = new long[] { 1, 3, VideoFrameUpscaler.Window, VideoFrameUpscaler.Window };
            if (imageInput.Value.ElementDataType == TensorElementType.Float16)
            {
                _halfInput = new Half[3 * VideoFrameUpscaler.Window * VideoFrameUpscaler.Window];
                _inputs.Add(imageInput.Key, MakeHalf(_halfInput, shape));
            }
            else if (imageInput.Value.ElementDataType == TensorElementType.Float)
            {
                _floatInput = new float[3 * VideoFrameUpscaler.Window * VideoFrameUpscaler.Window];
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
        if (_halfInput != null) for (int i = 0; i < input.Length; i++) _halfInput[i] = (Half)input[i];
        else input.CopyTo(_floatInput!, 0);
        using var options = new RunOptions();
        using var registration = ct.Register(() => options.Terminate = true);
        try
        {
            using var results = _session.Run(options, _inputs, _outputNames);
            var value = results[0];
            var shape = value.GetTensorTypeAndShape();
            if (!shape.Shape.SequenceEqual(new long[] { 1, 3, VideoFrameUpscaler.Window * 4, VideoFrameUpscaler.Window * 4 }))
                throw new InvalidOperationException("Unexpected super-resolution output shape");
            if (shape.ElementDataType == TensorElementType.Float16)
            {
                var values = value.GetTensorDataAsSpan<Float16>();
                for (int i = 0; i < output.Length; i++) output[i] = values[i].ToFloat();
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
