using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Runtime.ExceptionServices;

namespace FancyToolAva.Services;

#if WINDOWS
/// <summary>固定 GPU 输入/输出绑定；只在会话存储锁内创建，不下载像素。</summary>
internal sealed class VideoCudaTensor : IDisposable
{
    private readonly List<IDisposable> _owned = new();
    internal CudaVideoContext Pixels { get; }
    internal OrtIoBinding Binding { get; }
    internal nint Input { get; }
    internal nint Output { get; }
    internal VideoCudaTensor(InferenceSession session, VideoSrDevice device, int window)
    {
        try
        {
            Pixels = Own(new CudaVideoContext(device.TensorRtDeviceId!.Value));
            using var memory = OrtEnv.Instance().GetEpDevices().First(d => WindowsMlAccelerator.IsTensorRtProvider(d.EpName)
                && d.EpOptions.Entries.TryGetValue("device_id", out var id) && id == device.TensorRtDeviceId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .GetMemoryInfo(OrtDeviceMemoryType.DEFAULT);
            var allocator = Own(new OrtAllocator(session, memory));
            var input = Own(allocator.Allocate(checked((uint)window * (uint)window * 4)));
            var output = Own(allocator.Allocate(checked((uint)(window - 64) * (uint)(window - 64) * 16)));
            Input = input.DangerousGetHandle();
            Output = output.DangerousGetHandle();
            Binding = Own(session.CreateIoBinding());
            Binding.BindInput(session.InputNames.Single(), TensorElementType.UInt8, [1, window, window, 4], input);
            Binding.BindOutput(session.OutputNames.Single(), TensorElementType.Int32, [1, (window - 64) * 2, (window - 64) * 2], output);
        }
        catch { try { Dispose(); } catch { } throw; }
    }
    private T Own<T>(T value) where T : IDisposable { _owned.Add(value); return value; }
    public void Dispose()
    {
        ExceptionDispatchInfo? error = null;
        for (int i = _owned.Count - 1; i >= 0; i--)
            try { _owned[i].Dispose(); } catch (Exception ex) { error ??= ExceptionDispatchInfo.Capture(ex); }
        _owned.Clear();
        error?.Throw();
    }
}
#else
internal sealed class VideoCudaTensor : IDisposable
{
    internal CudaVideoContext Pixels => throw new PlatformNotSupportedException();
    internal OrtIoBinding Binding => throw new PlatformNotSupportedException();
    internal nint Input => throw new PlatformNotSupportedException();
    internal nint Output => throw new PlatformNotSupportedException();
    internal VideoCudaTensor(InferenceSession session, VideoSrDevice device, int window) => throw new PlatformNotSupportedException();
    public void Dispose() { }
}
#endif
