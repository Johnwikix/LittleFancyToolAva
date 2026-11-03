using Microsoft.ML.OnnxRuntime;
using FancyToolAva.Models;
using System.Runtime.ExceptionServices;

namespace FancyToolAva.Services;

/// <summary>隔离本机 TensorRT RTX 并发 Run 的画面损坏，其他后端保留独立执行。</summary>
internal sealed class VideoInferenceRunGuard(VideoSrDevice device)
{
    private static readonly ReaderWriterLockSlim RunGate = VideoGpuWorkGate.Gate;

    // 本机 EP 在两个 CUDA Graph 会话并发时产生黑帧；组合任务关闭捕获以恢复安全并发。
    internal static bool UseCudaGraph(VideoTranscodeOptions options) => !(options.HardwareBackend == HardwareBackend.Nvidia
        && Environment.GetEnvironmentVariable("FANCYTOOL_VIDEO_CUDA") != "0") && !(options.SuperResolutionEnabled
        && options.InterpolationMode == VideoInterpolationMode.Rife
        && !VideoSrModelCatalog.Get(options.SuperResolutionModel).RequiresDirectMl);

    internal void Run(InferenceSession session, RunOptions options, string[] inputNames, OrtValue[] inputs,
        string[] outputNames, OrtValue[] outputs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!device.UsesTensorRt)
        {
            session.Run(options, inputNames, inputs, outputNames, outputs);
            return;
        }
        // 仅隔离原生 Run；CPU 准备、复制、编解码及队列仍可重叠。等待也响应取消。
        using var scope = Enter(ct);
        ct.ThrowIfCancellationRequested();
        session.Run(options, inputNames, inputs, outputNames, outputs);
    }

    internal void RunBinding(InferenceSession session, RunOptions options, OrtIoBinding binding, CancellationToken ct, Action? bindInputs = null)
    {
        using var scope = Enter(ct);
        ct.ThrowIfCancellationRequested();
        ExceptionDispatchInfo? failure = null;
        try
        {
            bindInputs?.Invoke();
            binding.SynchronizeBoundInputs();
            session.RunWithBinding(options, binding);
        }
        catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        // 即使取消也要等待 GPU 完成，才允许释放或复用中间张量。
        try { binding.SynchronizeBoundInputs(); }
        catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        try { binding.SynchronizeBoundOutputs(); }
        catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        failure?.Throw();
    }

    private Scope Enter(CancellationToken ct)
    {
        if (!device.UsesTensorRt) return default;
        bool exclusive = device.UsesCudaGraph;
        while (!(exclusive ? RunGate.TryEnterWriteLock(25) : RunGate.TryEnterReadLock(25))) ct.ThrowIfCancellationRequested();
        return new Scope(exclusive ? 1 : 2);
    }

    private readonly struct Scope(int mode) : IDisposable
    {
        public void Dispose()
        {
            if (mode == 1) RunGate.ExitWriteLock();
            else if (mode == 2) RunGate.ExitReadLock();
        }
    }
}
