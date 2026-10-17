using System.Runtime.ExceptionServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FancyToolAva.Services;

#if WINDOWS
/// <summary>使用同一 NVIDIA EP 的 HOST_ACCESSIBLE 分配器优化主机传输。</summary>
internal sealed class VideoGpuHostMemory : IDisposable
{
    private readonly OrtMemoryInfo _memory;
    private readonly OrtAllocator _allocator;
    private bool _disposed;
    private VideoGpuHostMemory(OrtMemoryInfo memory, OrtAllocator allocator)
    {
        _memory = memory;
        _allocator = allocator;
    }

    internal static VideoGpuHostMemory? TryCreate(InferenceSession session, VideoSrDevice device)
    {
        if (!device.UsesTensorRt || device.TensorRtDeviceId is not { } id
            || Environment.GetEnvironmentVariable("FANCYTOOL_VIDEO_HOST_MEMORY") == "0") return null;
        OrtMemoryInfo? memory = null;
        OrtAllocator? allocator = null;
        bool transferred = false;
        try
        {
            var ep = OrtEnv.Instance().GetEpDevices().FirstOrDefault(d => WindowsMlAccelerator.IsTensorRtProvider(d.EpName)
                && d.EpOptions.Entries.TryGetValue("device_id", out string? value)
                && value == id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (ep == null) return null;
            memory = ep.GetMemoryInfo(OrtDeviceMemoryType.HOST_ACCESSIBLE);
            if (memory == null || memory.IsInvalid) return null;
            allocator = new OrtAllocator(session, memory);
            var result = new VideoGpuHostMemory(memory, allocator);
            transferred = true;
            return result;
        }
        catch (Exception error) when (error is OnnxRuntimeException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine(error);
            return null;
        }
        finally
        {
            if (!transferred)
            {
                try { allocator?.Dispose(); } catch (Exception cleanup) { System.Diagnostics.Debug.WriteLine(cleanup); }
                try { memory?.Dispose(); } catch (Exception cleanup) { System.Diagnostics.Debug.WriteLine(cleanup); }
            }
        }
    }

    internal OrtMemoryAllocation Allocate(uint bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _allocator.Allocate(bytes);
    }

    internal OrtValue Tensor(TensorElementType type, long[] shape, OrtMemoryAllocation allocation)
        => OrtValue.CreateTensorValueWithData(_memory, type, shape, allocation.DangerousGetHandle(), allocation.Size);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ExceptionDispatchInfo? failure = null;
        try { _allocator.Dispose(); } catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        try { _memory.Dispose(); } catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        failure?.Throw();
    }
}
#else
internal sealed class VideoGpuHostMemory : IDisposable
{
    internal static VideoGpuHostMemory? TryCreate(InferenceSession session, VideoSrDevice device) => null;
    internal OrtMemoryAllocation Allocate(uint bytes) => throw new PlatformNotSupportedException();
    internal OrtValue Tensor(TensorElementType type, long[] shape, OrtMemoryAllocation allocation) => throw new PlatformNotSupportedException();
    public void Dispose() { }
}
#endif
