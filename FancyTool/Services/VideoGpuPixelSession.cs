using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FancyToolAva.Services;

#if WINDOWS
/// <summary>前处理、原始 RIFE、像素打包通过 GPU 张量衔接；会话边界保留原始 FP32 舍入。</summary>
internal sealed unsafe class VideoGpuPixelSession : IDisposable
{
    private readonly List<IDisposable> _resources = new(16);
    private readonly InferenceSession _prepare, _network, _pack;
    private readonly OrtIoBinding _prepareBinding, _networkBinding, _packBinding;
    private readonly OrtMemoryAllocation _first, _second, _amount, _output;
    private readonly OrtMemoryAllocation _prepared, _inferred;
    private CudaVideoFrame _cudaFirst, _cudaSecond;
    private readonly CudaVideoContext _cudaPixels;
    private readonly VideoInferenceRunGuard _guard;
    private readonly Action _bindPrepare;
    private readonly long[] _byteShape;
    private readonly RunOptions _run;
    private bool _framesDirty = true;
    private bool _disposed;
    internal VideoSrDevice Device { get; }
    internal Span<byte> First => Bytes(_first);
    internal Span<byte> Second => Bytes(_second);
    internal ReadOnlySpan<byte> Output => Bytes(_output);

    internal VideoGpuPixelSession(string path, int width, int height, bool cudaGraph, CancellationToken ct)
    {
        try
        {
            _run = Own(new RunOptions());
            var parts = VideoPixelGraph.CreateStages(path, "rife", ct);
            _prepare = Own(WindowsMlAccelerator.CreateSession(parts.Before, VideoSrBackend.VendorGpu, width, height,
                ct, out var prepareDevice, true, useCudaGraph: false));
            _network = Own(WindowsMlAccelerator.CreateSession(path, VideoSrBackend.VendorGpu, width, height,
                ct, out var networkDevice, true, useCudaGraph: cudaGraph));
            _pack = Own(WindowsMlAccelerator.CreateSession(parts.After, VideoSrBackend.VendorGpu, width, height,
                ct, out var packDevice, true, useCudaGraph: cudaGraph));
            if (!networkDevice.UsesTensorRt || networkDevice.TensorRtDeviceId is not { } deviceId
                || prepareDevice.TensorRtDeviceId != deviceId || packDevice.TensorRtDeviceId != deviceId)
                throw new NotSupportedException("GPU pixel stages require one TensorRT RTX device");
            var input = _network.InputMetadata.Single();
            if (input.Value.ElementDataType != TensorElementType.Float || input.Value.Dimensions.Length != 4 || input.Value.Dimensions[1] != 7)
                throw new NotSupportedException(LocalizationRegistry.Get("VideoTranscode.Interp_InvalidModel"));
            Device = networkDevice with { UsesGpuPixels = true };
            _guard = new(Device);
            using var memory = OrtEnv.Instance().GetEpDevices().First(d => WindowsMlAccelerator.IsTensorRtProvider(d.EpName)
                && d.EpOptions.Entries.TryGetValue("device_id", out var id) && id == deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .GetMemoryInfo(OrtDeviceMemoryType.DEFAULT);
            var gpu = Own(new OrtAllocator(_network, memory));
            int plane = checked(width * height);
            _cudaPixels = Own(new CudaVideoContext(deviceId));
            _first = Own(OrtAllocator.DefaultInstance.Allocate(checked((uint)plane * 4)));
            _second = Own(OrtAllocator.DefaultInstance.Allocate(checked((uint)plane * 4)));
            _amount = Own(OrtAllocator.DefaultInstance.Allocate(4));
            _prepared = Own(gpu.Allocate(checked((uint)plane * 7 * 4)));
            _inferred = Own(gpu.Allocate(checked((uint)plane * 3 * 4)));
            _output = Own(OrtAllocator.DefaultInstance.Allocate(checked((uint)plane * 4)));
            _prepareBinding = Own(_prepare.CreateIoBinding());
            _networkBinding = Own(_network.CreateIoBinding());
            _packBinding = Own(_pack.CreateIoBinding());
            _byteShape = [1, height, width, 4];
            long[] inputShape = [1, 7, height, width], outputShape = [1, 3, height, width];
            _bindPrepare = BindPrepare;
            _prepareBinding.BindOutput(_prepare.OutputNames.Single(), TensorElementType.Float, inputShape, _prepared);
            _networkBinding.BindInput(_network.InputNames.Single(), TensorElementType.Float, inputShape, _prepared);
            _networkBinding.BindOutput(_network.OutputNames.Single(), TensorElementType.Float, outputShape, _inferred);
            _packBinding.BindInput(_pack.InputNames.Single(), TensorElementType.Float, outputShape, _inferred);
            _packBinding.BindOutput(_pack.OutputNames.Single(), TensorElementType.Int32, [1, height, width], _output);
            First.Clear();
            Second.Clear();
            SetAmount(.5f);
            Run(ct);
        }
        catch
        {
            try { Dispose(); } catch (Exception cleanup) { System.Diagnostics.Debug.WriteLine(cleanup); }
            throw;
        }
    }

    private T Own<T>(T value) where T : IDisposable { _resources.Add(value); return value; }
    private static Span<byte> Bytes(OrtMemoryAllocation allocation) => new((void*)allocation.DangerousGetHandle(), checked((int)allocation.Size));
    internal void SetAmount(float amount) => MemoryMarshal.Write(Bytes(_amount), in amount);
    internal void FramesChanged() => _framesDirty = true;

    internal void SetCudaFrames(CudaVideoFrame first, CudaVideoFrame second, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 只借用像素；阶段持有两幅 AVFrame，所有采样同步完成后才释放或替换。
        _cudaFirst = first;
        _cudaSecond = second;
    }

    internal void RunCuda(float amount, CudaVideoFrame destination, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cudaFirst.Data == 0 || _cudaSecond.Data == 0) throw new InvalidOperationException("CUDA RIFE frames were not set");
        ct.ThrowIfCancellationRequested();
        // 上层独占租约保证同一上下文串行；上一轮取消回调已随注册释放，再复位供下一帧使用。
        var run = _run;
        run.Terminate = false;
        using var registration = ct.Register(static state => ((RunOptions)state!).Terminate = true, run);
        long stamp = VideoPipelineMetrics.Start();
        _cudaPixels.PrepareRife(_cudaFirst, _cudaSecond, _prepared.DangerousGetHandle(), (int)_byteShape[2], (int)_byteShape[1], amount);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.RifeGpuPrepare, stamp);
        stamp = VideoPipelineMetrics.Start();
        _guard.RunBinding(_network, run, _networkBinding, ct);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.RifeNetwork, stamp);
        stamp = VideoPipelineMetrics.Start();
        _cudaPixels.PackRife(_inferred.DangerousGetHandle(), (int)_byteShape[2], (int)_byteShape[1], destination);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.RifeGpuPack, stamp);
        ct.ThrowIfCancellationRequested();
    }

    private void BindPrepare()
    {
        // CPU 输入在绑定时上传；每次内容变化都必须重新绑定。前处理不捕获可变化的上传地址。
        if (_framesDirty)
        {
            _prepareBinding.BindInput(VideoPixelGraph.Prefix + "first", TensorElementType.UInt8, _byteShape, _first);
            _prepareBinding.BindInput(VideoPixelGraph.Prefix + "second", TensorElementType.UInt8, _byteShape, _second);
            _framesDirty = false;
        }
        _prepareBinding.BindInput(VideoPixelGraph.Prefix + "amount", TensorElementType.Float, [1], _amount);
    }

    internal void Run(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        var run = _run;
        run.Terminate = false;
        using var registration = ct.Register(static state => ((RunOptions)state!).Terminate = true, run);
        long stamp = VideoPipelineMetrics.Start();
        _guard.RunBinding(_prepare, run, _prepareBinding, ct, _bindPrepare);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.RifeGpuPrepare, stamp);
        stamp = VideoPipelineMetrics.Start();
        _guard.RunBinding(_network, run, _networkBinding, ct);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.RifeNetwork, stamp);
        stamp = VideoPipelineMetrics.Start();
        _guard.RunBinding(_pack, run, _packBinding, ct);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.RifeGpuPack, stamp);
        ct.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ExceptionDispatchInfo? failure = null;
        for (int i = _resources.Count - 1; i >= 0; i--)
            try { _resources[i].Dispose(); }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        _resources.Clear();
        failure?.Throw();
    }
}
#else
// 非 Windows 构建不引用 Windows ML EP 设备及内存 API；硬件选择器不会进入该路径。
internal sealed class VideoGpuPixelSession : IDisposable
{
    private static PlatformNotSupportedException Unsupported() => new("Windows ML GPU pixel buffers require Windows.");
    internal VideoSrDevice Device => throw Unsupported();
    internal Span<byte> First => throw Unsupported();
    internal Span<byte> Second => throw Unsupported();
    internal ReadOnlySpan<byte> Output => throw Unsupported();
    internal VideoGpuPixelSession(string path, int width, int height, bool cudaGraph, CancellationToken ct) => throw Unsupported();
    internal void SetAmount(float amount) => throw Unsupported();
    internal void FramesChanged() => throw Unsupported();
    internal void SetCudaFrames(CudaVideoFrame first, CudaVideoFrame second, CancellationToken ct) => throw Unsupported();
    internal void RunCuda(float amount, CudaVideoFrame destination, CancellationToken ct) => throw Unsupported();
    internal void Run(CancellationToken ct) => throw Unsupported();
    public void Dispose() { }
}
#endif
