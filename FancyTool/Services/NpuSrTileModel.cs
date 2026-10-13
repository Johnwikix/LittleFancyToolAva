using System.Runtime.InteropServices;

namespace FancyToolAva.Services;

/// <summary>Compiles fixed-shape model tiles for Intel AI Boost and reuses the native inference tensors.</summary>
internal sealed unsafe class NpuSrTileModel : IVideoSrTileModel
{
    private readonly OpenVinoNative _api = OpenVinoNative.Instance;
    private nint _core, _model, _compiled, _request;
    private readonly List<nint> _inputs = [];
    private nint _output;
    private int _inputType, _outputType;
    private readonly int _window;

    internal NpuSrTileModel(string path, CancellationToken ct, int window = VideoFrameUpscaler.Window)
    {
        _window = window;
        try
        {
            ct.ThrowIfCancellationRequested();
            _api.Check(_api.CoreCreate(out _core));
            _api.Check(_api.AvailableDevices(_core, out var devices));
            try
            {
                bool npu = false;
                for (nuint i = 0; i < devices.Count; i++)
                    npu |= Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(devices.Names, checked((int)i * IntPtr.Size)))?.StartsWith("NPU", StringComparison.Ordinal) == true;
                if (!npu) throw new PlatformNotSupportedException("Intel AI Boost NPU or its driver is unavailable");
            }
            finally { _api.DevicesFree(ref devices); }
            _api.Check(_api.ModelRead(_core, path, null, out _model));
            var dimensions = stackalloc OpenVinoNative.Dimension[4];
            dimensions[0] = new() { Min = 1, Max = 1 };
            dimensions[1] = new() { Min = 3, Max = 3 };
            dimensions[2] = dimensions[3] = new() { Min = window, Max = window };
            var shape = new OpenVinoNative.PartialShape { Rank = new() { Min = 4, Max = 4 }, Dims = (nint)dimensions };
            nuint index = 0;
            _api.Check(_api.ModelReshape(_model, in index, in shape, 1));
            // The general model's denoise input also has a dynamic batch axis; NPU compilation needs it fixed.
            _api.Check(_api.ModelInputsCount(_model, out nuint modelInputs));
            var scalarShape = new OpenVinoNative.PartialShape { Rank = new() { Min = 1, Max = 1 }, Dims = (nint)dimensions };
            for (nuint i = 1; i < modelInputs; i++) _api.Check(_api.ModelReshape(_model, in i, in scalarShape, 1));
            string cache = Path.Combine(AppPaths.ModelsDirectory, "OpenVINO-cache");
            Directory.CreateDirectory(cache);
            _api.Check(_api.ModelCompile(_core, _model, "NPU", 2, out _compiled, "CACHE_DIR", cache));
            ct.ThrowIfCancellationRequested();
            _api.Check(_api.RequestCreate(_compiled, out _request));
            _api.Check(_api.InputsCount(_compiled, out nuint count));
            for (nuint i = 0; i < count; i++)
            {
                _api.Check(_api.InputTensor(_request, i, out nint tensor));
                _inputs.Add(tensor);
                if (i == 0)
                {
                    ValidateShape(tensor, [1, 3, window, window]);
                    _api.Check(_api.Type(tensor, out _inputType));
                }
                else
                {
                    ValidateShape(tensor, [1]);
                    _api.Check(_api.Type(tensor, out int type));
                    _api.Check(_api.Data(tensor, out nint data));
                    if (type == 3) *(Half*)data = (Half)0.5f;
                    else if (type == 4) *(float*)data = 0.5f;
                    else throw new NotSupportedException("Unsupported scalar precision");
                }
            }
            _api.Check(_api.OutputTensor(_request, 0, out _output));
            ValidateShape(_output, [1, 3, window * 4, window * 4]);
            _api.Check(_api.Type(_output, out _outputType));
            if (_inputType is not (3 or 4) || _outputType is not (3 or 4)) throw new NotSupportedException("Unsupported NPU model precision");
        }
        catch { Dispose(); throw; }
    }

    private void ValidateShape(nint tensor, long[] expected)
    {
        _api.Check(_api.TensorShape(tensor, out var shape));
        try
        {
            if (shape.Rank != expected.Length || !new ReadOnlySpan<long>((void*)shape.Dims, expected.Length).SequenceEqual(expected))
                throw new InvalidOperationException("Unexpected NPU tensor shape");
        }
        finally { _api.ShapeFree(ref shape); }
    }

    public void Infer(float[] input, float[] output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (input.Length != 3 * _window * _window || output.Length != input.Length * 16) throw new ArgumentException("Incorrect tile size");
        _api.Check(_api.Data(_inputs[0], out nint inputData));
        if (_inputType == 3) for (int i = 0; i < input.Length; i++) ((Half*)inputData)[i] = (Half)input[i];
        else input.AsSpan().CopyTo(new Span<float>((void*)inputData, input.Length));
        using (ct.Register(() => _api.Cancel(_request)))
        {
            int result = _api.Infer(_request);
            ct.ThrowIfCancellationRequested();
            _api.Check(result);
        }
        _api.Check(_api.Data(_output, out nint outputData));
        if (_outputType == 3) for (int i = 0; i < output.Length; i++) output[i] = (float)((Half*)outputData)[i];
        else new ReadOnlySpan<float>((void*)outputData, output.Length).CopyTo(output);
    }

    public void Dispose()
    {
        if (_output != 0) _api.TensorFree(_output);
        _output = 0;
        foreach (nint tensor in _inputs) _api.TensorFree(tensor);
        _inputs.Clear();
        if (_request != 0) _api.RequestFree(_request);
        if (_compiled != 0) _api.CompiledFree(_compiled);
        if (_model != 0) _api.ModelFree(_model);
        if (_core != 0) _api.CoreFree(_core);
        _request = _compiled = _model = _core = 0;
    }
}
