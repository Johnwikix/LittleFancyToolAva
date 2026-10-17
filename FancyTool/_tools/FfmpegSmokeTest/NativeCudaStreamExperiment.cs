using System.Runtime.InteropServices;
using FancyToolAva.Services;
using Microsoft.ML.OnnxRuntime;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private sealed class NativeCudaStreamExperiment : IDisposable
    {
        private readonly nint _context;
        internal readonly nint Handle;
        private bool _disposed;
        private readonly List<nint> _buffers = [];

        internal NativeCudaStreamExperiment()
        {
            Check(cuInit(0));
            Check(cuDevicePrimaryCtxRetain(out _context, 0));
            try { using var scope = Enter(); Check(cuStreamCreate(out Handle, 1)); }
            catch { cuDevicePrimaryCtxRelease(0); throw; }
        }
        internal IDisposable Enter()
        {
            Check(cuCtxPushCurrent(_context));
            return new ContextScope();
        }
        internal void Synchronize() => Check(cuStreamSynchronize(Handle));
        internal nint Allocate(int bytes)
        {
            using var scope = Enter();
            Check(cuMemAlloc(out var pointer, (ulong)bytes));
            _buffers.Add(pointer);
            return pointer;
        }
        internal unsafe void Upload(nint pointer, ReadOnlySpan<byte> bytes)
        {
            fixed (byte* data = bytes) Check(cuMemcpyHtoD(pointer, data, (ulong)bytes.Length));
        }
        internal unsafe void Download(nint pointer, Span<byte> bytes)
        {
            fixed (byte* data = bytes) Check(cuMemcpyDtoH(data, pointer, (ulong)bytes.Length));
        }
        internal InferenceSession CreateSession(string path, int width, int height)
        {
            var env = OrtEnv.Instance();
            var candidate = env.GetEpDevices().First(d => WindowsMlAccelerator.IsTensorRtProvider(d.EpName));
            using var options = new SessionOptions
            {
                EnableMemoryPattern = false, EnableCpuMemArena = false,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            foreach (string name in new[] { "height", "h", "in_height" }) options.AddFreeDimensionOverrideByName(name, height);
            foreach (string name in new[] { "width", "w", "in_width" }) options.AddFreeDimensionOverrideByName(name, width);
            foreach (string name in new[] { "batch", "batch_size" }) options.AddFreeDimensionOverrideByName(name, 1);
            options.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
            options.AppendExecutionProvider(env, [candidate], new Dictionary<string, string>
            {
                ["enable_cuda_graph"] = Environment.GetEnvironmentVariable("FANCYTOOL_TEST_NO_CUDA_GRAPH") == "1" ? "0" : "1",
                ["device_id"] = "0", ["user_compute_stream"] = Handle.ToString(),
                ["nv_runtime_cache_path"] = Path.Combine(AppPaths.ModelsDirectory, "Cache/TensorRT-RTX")
            });
            using var scope = Enter();
            return new InferenceSession(path, options);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                using var scope = Enter(); Synchronize();
                foreach (var pointer in _buffers) Check(cuMemFree(pointer));
                Check(cuStreamDestroy(Handle));
            }
            finally { Check(cuDevicePrimaryCtxRelease(0)); }
        }
        private sealed class ContextScope : IDisposable
        {
            public void Dispose() => Check(cuCtxPopCurrent(out _));
        }
        private static void Check(int error) { if (error != 0) throw new InvalidOperationException("CUDA driver error " + error); }
        [DllImport("nvcuda.dll")] private static extern int cuInit(uint flags);
        [DllImport("nvcuda.dll")] private static extern int cuDevicePrimaryCtxRetain(out nint context, int device);
        [DllImport("nvcuda.dll", EntryPoint = "cuDevicePrimaryCtxRelease_v2")] private static extern int cuDevicePrimaryCtxRelease(int device);
        [DllImport("nvcuda.dll", EntryPoint = "cuCtxPushCurrent_v2")] private static extern int cuCtxPushCurrent(nint context);
        [DllImport("nvcuda.dll", EntryPoint = "cuCtxPopCurrent_v2")] private static extern int cuCtxPopCurrent(out nint context);
        [DllImport("nvcuda.dll")] private static extern int cuStreamCreate(out nint stream, uint flags);
        [DllImport("nvcuda.dll")] private static extern int cuStreamSynchronize(nint stream);
        [DllImport("nvcuda.dll", EntryPoint = "cuStreamDestroy_v2")] private static extern int cuStreamDestroy(nint stream);
        [DllImport("nvcuda.dll", EntryPoint = "cuMemAlloc_v2")] private static extern int cuMemAlloc(out nint pointer, ulong bytes);
        [DllImport("nvcuda.dll", EntryPoint = "cuMemFree_v2")] private static extern int cuMemFree(nint pointer);
        [DllImport("nvcuda.dll", EntryPoint = "cuMemcpyHtoD_v2")] private static extern unsafe int cuMemcpyHtoD(nint device, void* host, ulong bytes);
        [DllImport("nvcuda.dll", EntryPoint = "cuMemcpyDtoH_v2")] private static extern unsafe int cuMemcpyDtoH(void* host, nint device, ulong bytes);
    }
}
