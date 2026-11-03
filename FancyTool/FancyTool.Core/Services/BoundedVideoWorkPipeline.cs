using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace FancyToolAva.Services;

/// <summary>固定缓冲区在准备、原生推理、回写之间流转；推理始终由调用线程串行执行。</summary>
internal static class BoundedVideoWorkPipeline
{
    internal static void Run<T>(IReadOnlyList<T> buffers, Func<T, CancellationToken, bool> prepare,
        Action<T, CancellationToken> infer, Action<T, CancellationToken> write, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var free = new BlockingCollection<T>(buffers.Count);
        using var ready = new BlockingCollection<T>(buffers.Count);
        using var completed = new BlockingCollection<T>(buffers.Count);
        foreach (var buffer in buffers) free.Add(buffer);
        ExceptionDispatchInfo? failure = null;
        var workers = new List<Task>(2);
        var metrics = VideoPipelineMetrics.Current;
        void Fail(Exception error)
        {
            Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(error), null);
            RequestStop();
        }
        void RequestStop()
        {
            try { stop.Cancel(); }
            catch (Exception error) { Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(error), null); }
        }
        void Start(Action action) => workers.Add(Task.Factory.StartNew(() =>
        {
            VideoPipelineMetrics.Current = metrics;
            try { action(); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) { Fail(error); }
            finally { VideoPipelineMetrics.Current = null; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        try
        {
            Start(() =>
            {
                while (true)
                {
                    var buffer = free.Take(stop.Token);
                    if (!prepare(buffer, stop.Token)) break;
                    ready.Add(buffer, stop.Token);
                }
                ready.CompleteAdding();
            });
            Start(() =>
            {
                foreach (var buffer in completed.GetConsumingEnumerable(stop.Token))
                {
                    write(buffer, stop.Token);
                    free.Add(buffer, stop.Token);
                }
            });
            foreach (var buffer in ready.GetConsumingEnumerable(stop.Token))
            {
                infer(buffer, stop.Token);
                completed.Add(buffer, stop.Token);
            }
            completed.CompleteAdding();
            Task.WaitAll(workers.ToArray());
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
        finally
        {
            RequestStop();
            Task.WaitAll(workers.ToArray());
        }
        failure?.Throw();
        ct.ThrowIfCancellationRequested();
    }
}
