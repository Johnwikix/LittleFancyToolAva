using System.Threading;

namespace FancyToolAva.Services;

/// <summary>同一进程的 NVIDIA 图捕获与外部 CUDA 工作共用门；普通 GPU 工作可并行。</summary>
internal static class VideoGpuWorkGate
{
    internal static readonly ReaderWriterLockSlim Gate = new();

    internal static Scope Enter(CancellationToken ct = default)
    {
        if (Gate.IsReadLockHeld || Gate.IsWriteLockHeld) return default;
        while (!Gate.TryEnterReadLock(25)) ct.ThrowIfCancellationRequested();
        return new Scope(true);
    }

    internal readonly struct Scope(bool acquired) : IDisposable
    {
        public void Dispose() { if (acquired) Gate.ExitReadLock(); }
    }
}
