using System.Runtime.ExceptionServices;
using Microsoft.ML.OnnxRuntime;
using SkiaSharp;
using System.Runtime.InteropServices;

namespace FancyToolAva.Services;

/// <summary>将通道转换、精度转换、量化与裁边交给 GPU；CPU 只保留有界字节缓冲。</summary>
internal sealed unsafe class OrtBgraVideoFrameUpscaler : IVideoFrameUpscaler, IVideoFrameDestinationUpscaler, IVideoCudaUpscaler
{
    private readonly InferenceSession _session;
    private readonly VideoInferenceRunGuard _guard;
    private readonly byte[]? _input;
    private readonly int[]? _output;
    private readonly VideoCudaTensor? _cuda;
    public bool SupportsCuda => _cuda != null;
    private readonly VideoGpuHostMemory? _host;
    private readonly OrtMemoryAllocation? _hostInput, _hostOutput;
    private Span<byte> InputPixels => _hostInput is { } input
        ? new Span<byte>((void*)input.DangerousGetHandle(), checked((int)input.Size)) : _input.AsSpan();
    private ReadOnlySpan<byte> OutputPixels => _hostOutput is { } output
        ? new ReadOnlySpan<byte>((void*)output.DangerousGetHandle(), checked((int)output.Size)) : MemoryMarshal.AsBytes(_output.AsSpan());
    private readonly OrtValue[] _inputs;
    private readonly OrtValue[] _outputs;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    private readonly int _window;
    private readonly int _tile;
    private readonly RunOptions _run;
    private bool _disposed;
    public VideoSrDevice Device { get; }

    internal OrtBgraVideoFrameUpscaler(string path, int window, bool half, CancellationToken ct, bool useCudaGraph = true, VideoSrBackend backend = VideoSrBackend.VendorGpu)
    {
        _window = window;
        _tile = window - 64;
        string adapted = VideoPixelGraph.Create(path, half ? "sr16" : "sr32", ct);
        _session = WindowsMlAccelerator.CreateSession(adapted, backend, window, window, ct,
            out var device, requireHardware: true, useFp16: half, useCudaGraph: useCudaGraph);
        Device = device with { UsesGpuPixels = true };
        _guard = new(device);
        try
        {
            if (!device.UsesTensorRt && backend != VideoSrBackend.DirectMl)
                throw new NotSupportedException("GPU pixel adapters require TensorRT RTX or DirectML");
            // RAVU R3 的配对测试中该主机分配器降低吞吐，保留普通缓冲。
            _host = Path.GetFileName(path) == "ravu_lite_r3_fp32.onnx" ? null : VideoGpuHostMemory.TryCreate(_session, device);
            if (_host != null)
            {
                _hostInput = _host.Allocate(checked((uint)window * (uint)window * 4));
                _hostOutput = _host.Allocate(checked((uint)_tile * (uint)_tile * 16));
                InputPixels.Clear();
                _inputs = [_host.Tensor(Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.UInt8, [1, window, window, 4], _hostInput)];
                _outputs = [_host.Tensor(Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Int32, [1, _tile * 2, _tile * 2], _hostOutput)];
                Device = Device with { UsesGpuHostMemory = true };
            }
            else
            {
                _input = new byte[checked(window * window * 4)];
                _output = new int[checked(_tile * _tile * 4)];
                _inputs = [OrtValue.CreateTensorValueFromMemory(_input, [1, window, window, 4])];
                _outputs = [OrtValue.CreateTensorValueFromMemory(_output, [1, _tile * 2, _tile * 2])];
            }
            if (device.UsesTensorRt && Environment.GetEnvironmentVariable("FANCYTOOL_VIDEO_CUDA") != "0")
                _cuda = new VideoCudaTensor(_session, device, window);
            _inputNames = [_session.InputNames.Single()];
            _outputNames = [_session.OutputNames.Single()];
            _run = new RunOptions();
            // 在模型存储锁内完成首次原生执行，提前验证像素算子和 GPU 缓存，避免迁移后才懒初始化。
            using var registration = ct.Register(static state => ((RunOptions)state!).Terminate = true, _run);
            try { _guard.Run(_session, _run, _inputNames, _inputs, _outputNames, _outputs, ct); }
            catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        }
        catch
        {
            try { Dispose(); } catch (Exception cleanup) { System.Diagnostics.Debug.WriteLine(cleanup); }
            throw;
        }
    }

    public SKBitmap Upscale(SKBitmap source, int scale, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrameUpscaler.ValidateSize(source.Width, source.Height, scale);
        if (scale != 2) throw new ArgumentOutOfRangeException(nameof(scale));
        var result = new SKBitmap(source.Width * 2, source.Height * 2, SKColorType.Bgra8888, SKAlphaType.Opaque);
        try { TryUpscaleInto(source, scale, result, ct); return result; }
        catch { result.Dispose(); throw; }
    }

    public bool TryUpscaleInto(SKBitmap source, int scale, SKBitmap result, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrameUpscaler.ValidateSize(source.Width, source.Height, scale);
        if (scale != 2 || result.Width != source.Width * 2 || result.Height != source.Height * 2 || result.ColorType != SKColorType.Bgra8888)
            throw new ArgumentException("Invalid destination bitmap");
        ct.ThrowIfCancellationRequested();
        _run.Terminate = false;
        using var registration = ct.Register(static state => ((RunOptions)state!).Terminate = true, _run);
        try
        {
            for (int y = 0; y < source.Height; y += _tile)
            for (int x = 0; x < source.Width; x += _tile)
            {
                RunTile(source, result, x, y, ct);
            }
            return true;
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    internal void ProcessTile(SKBitmap source, SKBitmap result, int x, int y, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _run.Terminate = false;
        using var registration = ct.Register(static state => ((RunOptions)state!).Terminate = true, _run);
        try { RunTile(source, result, x, y, ct); }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    private void RunTile(SKBitmap source, SKBitmap result, int x, int y, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long stamp = VideoPipelineMetrics.Start();
        VideoBgraPixels.CopyPadded(source, x, y, _window, _window, 32, InputPixels, ct);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrPrepare, stamp);
        stamp = VideoPipelineMetrics.Start();
        _guard.Run(_session, _run, _inputNames, _inputs, _outputNames, _outputs, ct);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrNativeRun, stamp);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrRun, stamp);
        stamp = VideoPipelineMetrics.Start();
        VideoBgraPixels.Paste(OutputPixels, _tile * 8, result, x * 2, y * 2,
            Math.Min(_tile, source.Width - x) * 2, Math.Min(_tile, source.Height - y) * 2, ct);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrPaste, stamp);
    }

    public void UpscaleCuda(CudaVideoFrame source, CudaVideoFrame destination, int scale, CancellationToken ct)
    {
        if (scale != 2 || destination.Width != source.Width * 2 || destination.Height != source.Height * 2)
            throw new ArgumentException("Invalid CUDA SR destination");
        for (int y = 0; y < source.Height; y += _tile)
        for (int x = 0; x < source.Width; x += _tile) ProcessCudaTile(source, destination, x, y, ct);
    }

    internal void ProcessCudaTile(CudaVideoFrame source, CudaVideoFrame result, int x, int y, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cuda = _cuda ?? throw new NotSupportedException();
        ct.ThrowIfCancellationRequested();
        _run.Terminate = false;
        using var registration = ct.Register(static state => ((RunOptions)state!).Terminate = true, _run);
        long stamp = VideoPipelineMetrics.Start();
        cuda.Pixels.Pad(source, cuda.Input, _window, _window, x - 32, y - 32);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrPrepare, stamp);
        stamp = VideoPipelineMetrics.Start();
        try { _guard.RunBinding(_session, _run, cuda.Binding, ct); }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrNativeRun, stamp);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrRun, stamp);
        stamp = VideoPipelineMetrics.Start();
        cuda.Pixels.Paste(cuda.Output, _tile * 8, result, Math.Min(_tile, source.Width - x) * 2,
            Math.Min(_tile, source.Height - y) * 2, x * 2, y * 2);
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrPaste, stamp);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ExceptionDispatchInfo? failure = null;
        void Release(IDisposable? value)
        {
            try { value?.Dispose(); }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        }
        if (_inputs != null) foreach (var value in _inputs) Release(value);
        if (_outputs != null) foreach (var value in _outputs) Release(value);
        Release(_run);
        Release(_hostInput);
        Release(_hostOutput);
        Release(_host);
        Release(_cuda);
        Release(_session);
        failure?.Throw();
    }
}
