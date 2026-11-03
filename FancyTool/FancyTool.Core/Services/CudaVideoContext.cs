using System.Runtime.InteropServices;

namespace FancyToolAva.Services;

/// <summary>只借用 CUDA BGRA 像素；调用方持有 AVFrame/张量引用直到同步操作完成。</summary>
internal readonly record struct CudaVideoFrame(nint Data, int Width, int Height, int Pitch);

internal interface IVideoCudaUpscaler
{
    bool SupportsCuda { get; }
    void UpscaleCuda(CudaVideoFrame source, CudaVideoFrame destination, int scale, CancellationToken ct);
}

internal interface IVideoCudaInterpolator
{
    bool SupportsCuda { get; }
    void SetCudaFrames(CudaVideoFrame first, CudaVideoFrame second, CancellationToken ct);
    void InterpolateCuda(double amount, CudaVideoFrame destination, CancellationToken ct);
}

/// <summary>和 Windows ML 共用设备主上下文；内核只在自身流执行，同步后才交还借用缓冲。</summary>
internal sealed unsafe partial class CudaVideoContext : IDisposable
{
    private nint _context, _module, _stream;
    private readonly nint _pad, _paste, _nv12, _toNv12, _compare, _preview, _rifePrepare, _rifePack;
    private ulong _comparison;
    private nint _comparisonHost;
    private readonly object _comparisonGate = new();
    private bool _disposed;
    internal int DeviceId { get; }
    internal nint Stream => _stream;

    internal CudaVideoContext(int deviceId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        DeviceId = deviceId;
        Check(cuInit(0));
        Check(cuDevicePrimaryCtxRetain(out _context, deviceId));
        try
        {
            using var scope = Enter();
            Check(cuStreamCreate(out _stream, 1));
            _module = LoadModule("pixels");
            Check(cuModuleGetFunction(out _pad, _module, "pad"));
            Check(cuModuleGetFunction(out _paste, _module, "paste"));
            Check(cuModuleGetFunction(out _nv12, _module, "nv12"));
            Check(cuModuleGetFunction(out _toNv12, _module, "to_nv12"));
            Check(cuModuleGetFunction(out _rifePrepare, _module, "rife_prepare"));
            Check(cuModuleGetFunction(out _rifePack, _module, "rife_pack"));
            Check(cuModuleGetFunction(out _preview, _module, "preview"));
            Check(cuModuleGetFunction(out _compare, _module, "compare"));
            Check(cuMemAlloc(out _comparison, 8));
            Check(cuMemHostAlloc(out _comparisonHost, 8, 1));
        }
        catch { try { Dispose(); } catch { } throw; }
    }

    internal Scope Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lease = VideoGpuWorkGate.Enter();
        try
        {
            Check(cuCtxPushCurrent(_context));
            return new Scope(lease);
        }
        catch { lease.Dispose(); throw; }
    }

    internal readonly struct Scope(VideoGpuWorkGate.Scope lease) : IDisposable
    {
        public void Dispose()
        {
            try { Check(cuCtxPopCurrent(out _)); }
            finally { lease.Dispose(); }
        }
    }

    private void Launch(nint function, int width, int height, void** args)
    {
        Check(cuLaunchKernel(function, (uint)((width + 31) / 32), (uint)((height + 7) / 8), 1, 32, 8, 1, 0, _stream, args, null));
        // ORT、NVDEC、NVENC 的流不归此对象所有；在交接处完成本流，避免复用在途像素。
        Check(cuStreamSynchronize(_stream));
    }

    internal void WaitDecoder(nint decoderStream)
    {
        using var scope = Enter();
        // 帧交接只等待 NVDEC 的专用流，不阻塞其他会话的非阻塞推理流。
        Check(cuStreamSynchronize(decoderStream));
    }

    internal void Pad(CudaVideoFrame source, nint output, int width, int height, int x, int y)
    {
        using var scope = Enter();
        nint input = source.Data; int pitch = source.Pitch / 4, sw = source.Width, sh = source.Height;
        void** args = stackalloc void*[9] { &input, &pitch, &sw, &sh, &output, &width, &height, &x, &y };
        Launch(_pad, width, height, args);
    }

    internal void Paste(nint input, int sourcePitch, CudaVideoFrame output, int width, int height, int x = 0, int y = 0)
    {
        using var scope = Enter();
        nint dst = output.Data; int sp = sourcePitch / 4, dp = output.Pitch / 4;
        void** args = stackalloc void*[8] { &input, &sp, &dst, &dp, &width, &height, &x, &y };
        Launch(_paste, width, height, args);
    }

    internal void ConvertNv12(nint y, nint uv, int yp, int up, CudaVideoFrame output, bool bt709, bool full)
    {
        using var scope = Enter();
        nint dst = output.Data; int dp = output.Pitch / 4, w = output.Width, h = output.Height, matrix = bt709 ? 1 : 0, range = full ? 1 : 0;
        void** args = stackalloc void*[10] { &y, &uv, &yp, &up, &dst, &dp, &w, &h, &matrix, &range };
        Launch(_nv12, w, h, args);
    }

    internal void ConvertToNv12(CudaVideoFrame input, nint y, nint uv, int yp, int up, bool bt709)
    {
        using var scope = Enter();
        nint src = input.Data; int sp = input.Pitch / 4, w = input.Width, h = input.Height, matrix = bt709 ? 1 : 0;
        void** args = stackalloc void*[9] { &src, &sp, &y, &uv, &yp, &up, &w, &h, &matrix };
        Launch(_toNv12, w, h, args);
    }

    internal bool HoldPair(CudaVideoFrame first, CudaVideoFrame second, bool detectScenes)
    {
        if (first == second) return true;
        if (first.Width != second.Width || first.Height != second.Height) throw new ArgumentException("CUDA pair size mismatch");
        lock (_comparisonGate)
        {
            using var scope = Enter();
            Check(cuMemsetD32Async(_comparison, 0, 2, _stream));
            nint a = first.Data, b = second.Data; int ap = first.Pitch / 4, bp = second.Pitch / 4, w = first.Width, h = first.Height, scene = detectScenes ? 1 : 0;
            ulong result = _comparison;
            void** args = stackalloc void*[8] { &a, &ap, &b, &bp, &w, &h, &result, &scene };
            Launch(_compare, w, h, args);
            Check(cuMemcpyDtoHAsync((void*)_comparisonHost, _comparison, 8, _stream));
            Check(cuStreamSynchronize(_stream));
            uint* flags = (uint*)_comparisonHost;
            return flags[0] == 0 || flags[1] != 0;
        }
    }

    internal void PrepareRife(CudaVideoFrame first, CudaVideoFrame second, nint output, int width, int height, float amount)
    {
        using var scope = Enter();
        nint a = first.Data, b = second.Data;
        int ap = first.Pitch / 4, bp = second.Pitch / 4, sw = first.Width, sh = first.Height;
        void** args = stackalloc void*[10] { &a, &ap, &b, &bp, &sw, &sh, &output, &width, &height, &amount };
        Launch(_rifePrepare, width, height, args);
    }

    internal void PackRife(nint input, int inputWidth, int inputHeight, CudaVideoFrame output)
    {
        using var scope = Enter();
        nint dst = output.Data;
        int dp = output.Pitch / 4, w = output.Width, h = output.Height;
        void** args = stackalloc void*[7] { &input, &inputWidth, &inputHeight, &dst, &dp, &w, &h };
        Launch(_rifePack, w, h, args);
    }

    internal void Preview(nint input, nint uv, int pitch, int uvPitch, int width, int height, CudaVideoFrame output,
        bool nv12, bool bt709, bool full, Span<byte> pixels)
    {
        using var scope = Enter();
        int dw = output.Width, dh = output.Height, nv = nv12 ? 1 : 0, matrix = bt709 ? 1 : 0, range = full ? 1 : 0;
        nint dst = output.Data;
        void** args = stackalloc void*[12] { &input, &uv, &pitch, &uvPitch, &width, &height, &dst, &dw, &dh, &nv, &matrix, &range };
        Launch(_preview, dw, dh, args);
        fixed (byte* cpu = pixels) Check(cuMemcpyDtoH(cpu, (ulong)output.Data, (nuint)pixels.Length));
    }

    internal CudaBuffer Allocate(int width, int height)
    {
        using var scope = Enter();
        Check(cuMemAlloc(out var data, checked((nuint)width * (nuint)height * 4)));
        return new CudaBuffer(this, new((nint)data, width, height, checked(width * 4)));
    }

    internal sealed class CudaBuffer(CudaVideoContext owner, CudaVideoFrame frame) : IDisposable
    {
        private bool _disposed;
        internal CudaVideoFrame Frame => frame;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            using var scope = owner.Enter();
            Check(cuMemFree((ulong)frame.Data));
        }
    }

    internal static void Check(int result)
    {
        if (result != 0) throw new InvalidOperationException($"CUDA driver error {result}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        Exception? failure = null;
        void Release(int result) { if (result != 0) failure ??= new InvalidOperationException($"CUDA cleanup error {result}"); }
        if (_context != 0)
        {
            using (Enter())
            {
                if (_stream != 0) Release(cuStreamSynchronize(_stream));
                if (_comparison != 0) Release(cuMemFree(_comparison));
                if (_comparisonHost != 0) Release(cuMemFreeHost(_comparisonHost));
                if (_hdrModule != 0) Release(cuModuleUnload(_hdrModule));
                if (_module != 0) Release(cuModuleUnload(_module));
                if (_stream != 0) Release(cuStreamDestroy(_stream));
            }
            Release(cuDevicePrimaryCtxRelease(DeviceId));
        }
        _disposed = true;
        _context = _module = _stream = _hdrModule = 0;
        _comparison = 0;
        if (failure != null) throw failure;
    }

    [DllImport("nvcuda.dll")] private static extern int cuInit(uint flags);
    [DllImport("nvcuda.dll")] private static extern int cuDevicePrimaryCtxRetain(out nint context, int device);
    [DllImport("nvcuda.dll", EntryPoint = "cuDevicePrimaryCtxRelease_v2")] private static extern int cuDevicePrimaryCtxRelease(int device);
    [DllImport("nvcuda.dll", EntryPoint = "cuCtxPushCurrent_v2")] private static extern int cuCtxPushCurrent(nint context);
    [DllImport("nvcuda.dll", EntryPoint = "cuCtxPopCurrent_v2")] private static extern int cuCtxPopCurrent(out nint context);
    [DllImport("nvcuda.dll")] private static extern int cuStreamCreate(out nint stream, uint flags);
    [DllImport("nvcuda.dll")] private static extern int cuStreamSynchronize(nint stream);
    [DllImport("nvcuda.dll", EntryPoint = "cuStreamDestroy_v2")] private static extern int cuStreamDestroy(nint stream);
    [DllImport("nvcuda.dll")] private static extern int cuModuleLoadData(out nint module, void* data);
    [DllImport("nvcuda.dll")] private static extern int cuModuleGetFunction(out nint function, nint module, [MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport("nvcuda.dll")] private static extern int cuModuleUnload(nint module);
    [DllImport("nvcuda.dll")] private static extern int cuLaunchKernel(nint function, uint gx, uint gy, uint gz, uint bx, uint by, uint bz, uint shared, nint stream, void** parameters, void** extra);
    [DllImport("nvcuda.dll", EntryPoint = "cuMemAlloc_v2")] private static extern int cuMemAlloc(out ulong data, nuint bytes);
    [DllImport("nvcuda.dll", EntryPoint = "cuMemFree_v2")] private static extern int cuMemFree(ulong data);
    [DllImport("nvcuda.dll")] private static extern int cuMemsetD32Async(ulong data, uint value, nuint count, nint stream);
    [DllImport("nvcuda.dll")] private static extern int cuMemHostAlloc(out nint data, nuint size, uint flags);
    [DllImport("nvcuda.dll")] private static extern int cuMemFreeHost(nint data);
    [DllImport("nvcuda.dll", EntryPoint = "cuMemcpyDtoHAsync_v2")] private static extern int cuMemcpyDtoHAsync(void* dst, ulong src, nuint size, nint stream);
    [DllImport("nvcuda.dll", EntryPoint = "cuMemcpyDtoH_v2")] private static extern int cuMemcpyDtoH(void* dst, ulong src, nuint size);
}
