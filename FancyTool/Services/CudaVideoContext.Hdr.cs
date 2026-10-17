using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;

namespace FancyToolAva.Services;

internal sealed unsafe partial class CudaVideoContext
{
    private readonly object _hdrGate = new();
    private nint _hdrModule, _hdrBgra, _hdrYuv, _hdrPreview;

    private void EnsureHdr()
    {
        lock (_hdrGate)
        {
            if (_hdrModule != 0) return;
            nint module = LoadModule("hdr");
            try
            {
                Check(cuModuleGetFunction(out _hdrBgra, module, "hdr_bgra"));
                Check(cuModuleGetFunction(out _hdrYuv, module, "hdr_yuv"));
                Check(cuModuleGetFunction(out _hdrPreview, module, "hdr_preview"));
                _hdrModule = module;
            }
            catch { cuModuleUnload(module); throw; }
        }
    }

    private static nint LoadModule(string name)
    {
        using var resource = typeof(CudaVideoContext).Assembly.GetManifestResourceStream($"FancyToolAva.VideoCuda.{name}.ptx")
            ?? throw new InvalidOperationException($"Missing CUDA module {name}");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        byte[] ptx = [.. bytes.ToArray(), 0];
        fixed (byte* p = ptx) { Check(cuModuleLoadData(out var module, p)); return module; }
    }

    internal CudaBuffer UploadLut(ReadOnlySpan<float> values)
    {
        var buffer = Allocate(values.Length, 1);
        try
        {
            using var scope = Enter();
            nint host = 0;
            ExceptionDispatchInfo? failure = null;
            try
            {
                Check(cuMemHostAlloc(out host, checked((nuint)values.Length * 4), 1));
                values.CopyTo(new Span<float>((void*)host, values.Length));
                // 分页主机内存的同步 HtoD 不保证 DMA 已完成；固定页缓冲与消费内核使用同一流。
                Check(cuMemcpyHtoDAsync((ulong)buffer.Frame.Data, (void*)host, checked((nuint)values.Length * 4), _stream));
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            try { Check(cuStreamSynchronize(_stream)); }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
            try { if (host != 0) Check(cuMemFreeHost(host)); }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
            failure?.Throw();
            return buffer;
        }
        catch { try { buffer.Dispose(); } catch (Exception cleanup) { System.Diagnostics.Debug.WriteLine(cleanup); } throw; }
    }

    internal void HdrBgra(nint y, nint uv, int yp, int up, int kind, int matrix, bool full, nint lut, int size, CudaVideoFrame output)
    {
        using var scope = Enter();
        EnsureHdr();
        int w = output.Width, h = output.Height, range = full ? 1 : 0, dp = output.Pitch / 4;
        nint dst = output.Data;
        void** args = stackalloc void*[13] { &y, &uv, &yp, &up, &w, &h, &kind, &matrix, &range, &lut, &size, &dst, &dp };
        Launch(_hdrBgra, w, h, args);
    }

    internal void HdrYuv(nint y, nint uv, int yp, int up, int w, int h, int kind, int matrix, bool full, nint lut, int size,
        nint dst, nint dstUv, int dp, int dup, bool tenBit, int outputMatrix)
    {
        using var scope = Enter();
        EnsureHdr();
        int range = full ? 1 : 0, ten = tenBit ? 1 : 0;
        void** args = stackalloc void*[17] { &y, &uv, &yp, &up, &w, &h, &kind, &matrix, &range, &lut, &size,
            &dst, &dstUv, &dp, &dup, &ten, &outputMatrix };
        Launch(_hdrYuv, w, h, args);
    }

    internal void HdrPreview(nint y, nint uv, int yp, int up, int w, int h, int kind, int matrix, bool full, nint lut, int size,
        CudaVideoFrame output, Span<byte> pixels)
    {
        using var scope = Enter();
        EnsureHdr();
        int range = full ? 1 : 0, dw = output.Width, dh = output.Height;
        nint dst = output.Data;
        void** args = stackalloc void*[14] { &y, &uv, &yp, &up, &w, &h, &kind, &matrix, &range, &lut, &size, &dst, &dw, &dh };
        Launch(_hdrPreview, dw, dh, args);
        fixed (byte* cpu = pixels) Check(cuMemcpyDtoH(cpu, (ulong)dst, (nuint)pixels.Length));
    }

    [DllImport("nvcuda.dll", EntryPoint = "cuMemcpyHtoDAsync_v2")] private static extern int cuMemcpyHtoDAsync(ulong dst, void* src, nuint size, nint stream);
}
