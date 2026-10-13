using System.Runtime.InteropServices;

namespace FancyToolAva.Services;

/// <summary>Binds the bundled OpenVINO C API independently of ONNX Runtime's native distribution.</summary>
internal sealed class OpenVinoNative
{
    private static readonly Lazy<OpenVinoNative> InstanceHolder = new(() => new OpenVinoNative());
    internal static OpenVinoNative Instance => InstanceHolder.Value;
    private readonly nint _library;

    [StructLayout(LayoutKind.Sequential)] internal struct Dimension { public long Min, Max; }
    [StructLayout(LayoutKind.Sequential)] internal struct PartialShape { public Dimension Rank; public nint Dims; }
    [StructLayout(LayoutKind.Sequential)] internal struct Shape { public long Rank; public nint Dims; }
    [StructLayout(LayoutKind.Sequential)] internal struct Devices { public nint Names; public nuint Count; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Create(out nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Release(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GetDevices(nint core, out Devices devices);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FreeDevices(ref Devices devices);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ReadModel(nint core, [MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPWStr)] string? weights, out nint model);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Reshape(nint model, in nuint index, in PartialShape shape, nuint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Compile(nint core, nint model, [MarshalAs(UnmanagedType.LPUTF8Str)] string device,
        nuint propertyArguments, out nint compiled, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CreateRequest(nint compiled, out nint request);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int InputCount(nint model, out nuint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GetTensor(nint request, nuint index, out nint tensor);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GetShape(nint tensor, out Shape shape);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int FreeShape(ref Shape shape);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int TensorData(nint tensor, out nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int TensorType(nint tensor, out int type);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Run(nint request);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ErrorMessage();

    internal readonly Create CoreCreate;
    internal readonly Release CoreFree, ModelFree, CompiledFree, RequestFree, TensorFree;
    internal readonly GetDevices AvailableDevices;
    internal readonly FreeDevices DevicesFree;
    internal readonly ReadModel ModelRead;
    internal readonly Reshape ModelReshape;
    internal readonly Compile ModelCompile;
    internal readonly CreateRequest RequestCreate;
    internal readonly InputCount InputsCount;
    internal readonly InputCount ModelInputsCount;
    internal readonly GetTensor InputTensor, OutputTensor;
    internal readonly GetShape TensorShape;
    internal readonly FreeShape ShapeFree;
    internal readonly TensorData Data;
    internal readonly TensorType Type;
    internal readonly Run Infer, Cancel;
    private readonly ErrorMessage _lastError;

    private OpenVinoNative()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("Intel NPU runtime requires Windows x64");
        string directory = Path.Combine(AppContext.BaseDirectory, "OpenVINO");
        // Keep native modules loaded for the process lifetime, including their plugin registry.
        NativeLibrary.Load(Path.Combine(directory, "tbb12.dll"));
        NativeLibrary.Load(Path.Combine(directory, "openvino.dll"));
        _library = NativeLibrary.Load(Path.Combine(directory, "openvino_c.dll"));
        CoreCreate = Bind<Create>("ov_core_create");
        CoreFree = Bind<Release>("ov_core_free");
        ModelFree = Bind<Release>("ov_model_free");
        CompiledFree = Bind<Release>("ov_compiled_model_free");
        RequestFree = Bind<Release>("ov_infer_request_free");
        TensorFree = Bind<Release>("ov_tensor_free");
        AvailableDevices = Bind<GetDevices>("ov_core_get_available_devices");
        DevicesFree = Bind<FreeDevices>("ov_available_devices_free");
        ModelRead = Bind<ReadModel>("ov_core_read_model_unicode");
        ModelReshape = Bind<Reshape>("ov_model_reshape_by_port_indexes");
        ModelCompile = Bind<Compile>("ov_core_compile_model");
        RequestCreate = Bind<CreateRequest>("ov_compiled_model_create_infer_request");
        InputsCount = Bind<InputCount>("ov_compiled_model_inputs_size");
        ModelInputsCount = Bind<InputCount>("ov_model_inputs_size");
        InputTensor = Bind<GetTensor>("ov_infer_request_get_input_tensor_by_index");
        OutputTensor = Bind<GetTensor>("ov_infer_request_get_output_tensor_by_index");
        TensorShape = Bind<GetShape>("ov_tensor_get_shape");
        ShapeFree = Bind<FreeShape>("ov_shape_free");
        Data = Bind<TensorData>("ov_tensor_data");
        Type = Bind<TensorType>("ov_tensor_get_element_type");
        Infer = Bind<Run>("ov_infer_request_infer");
        Cancel = Bind<Run>("ov_infer_request_cancel");
        _lastError = Bind<ErrorMessage>("ov_get_last_err_msg");
    }

    private T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    internal void Check(int result)
    {
        if (result != 0) throw new InvalidOperationException($"OpenVINO ({result}): {Marshal.PtrToStringUTF8(_lastError())}");
    }
}
