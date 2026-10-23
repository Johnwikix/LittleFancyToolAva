using System.Diagnostics;
using System.Globalization;

namespace FancyToolAva.Services;

// 仅在诊断时启用；记录墙钟耗时，Run 包含 GPU 执行及传输，并不等于 GPU 内核耗时。
internal sealed class VideoPipelineMetrics : IDisposable
{
    internal enum Phase { Decode, Encode, SrPrepare, SrRun, SrPaste, RifePrepare, RifeRun, RifeReadback,
        DecodeQueueWrite, SrQueueRead, SrQueueWrite, RifeQueueRead, RifeQueueWrite, EncodeQueueRead,
        SrTensorPrepare, SrNativeRun, SrTensorRead, FrameAllocate, FrameReuse, RifeGpuPrepare, RifeNetwork, RifeGpuPack, SrReuseCheck, SrFrameReuse, CudaDecode, CudaToBgra, CudaToNv12, DecodeDownload, DecodeSubmit, CudaHdrToneMap, CudaToP010, HdrLut, ImageAlpha, ImageResize }
    [ThreadStatic] internal static VideoPipelineMetrics? Current;
    private readonly long[] _ticks = new long[Enum.GetValues<Phase>().Length];
    private readonly long[] _calls = new long[Enum.GetValues<Phase>().Length];
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly string _directory;
    private VideoPipelineMetrics(string directory) => _directory = directory;
    internal static VideoPipelineMetrics? Begin()
    {
        string? directory = Environment.GetEnvironmentVariable("FANCYTOOL_VIDEO_PROFILE");
        return Current = string.IsNullOrWhiteSpace(directory) ? null : new(directory);
    }
    internal static long Start() => Current == null ? 0 : Stopwatch.GetTimestamp();
    internal static void Record(Phase phase, long start)
    {
        if (Current is not { } metrics || start == 0) return;
        Interlocked.Add(ref metrics._ticks[(int)phase], Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref metrics._calls[(int)phase]);
    }
    public void Dispose()
    {
        Current = null;
        var lines = new List<string> { "phase,calls,totalMs", FormattableString.Invariant($"Wall,1,{Stopwatch.GetElapsedTime(_start).TotalMilliseconds:F3}") };
        foreach (var phase in Enum.GetValues<Phase>())
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{phase},{_calls[(int)phase]},{_ticks[(int)phase] * 1000d / Stopwatch.Frequency:F3}"));
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllLines(Path.Combine(_directory, $"pipeline-{Guid.NewGuid():N}.csv"), lines);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 诊断输出失败不能妨碍原生资源收尾，也不能使成功的转码变成失败。
            Debug.WriteLine(error);
        }
    }
}
