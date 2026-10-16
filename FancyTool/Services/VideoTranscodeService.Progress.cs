using System.Diagnostics;
using FancyToolAva.Models;

namespace FancyToolAva.Services;

public sealed unsafe partial class VideoTranscodeService
{
    /// <summary>Reuses a container's frame count only for a complete, frame-count-preserving conversion.</summary>
    internal static long? ExactOutputFrameCount(long sourceFrames, VideoTranscodeOptions options)
        => sourceFrames > 0 && options.TrimStartSeconds == 0 && options.TrimEndSeconds == null
            && options.InterpolationMode == VideoInterpolationMode.None && options.FpsMode == FpsMode.SameAsSource
            && options.Deinterlace == DeinterlaceMode.None && options.Container != VideoContainer.Gif
                ? sourceFrames : null;

    /// <summary>Measures output frame throughput for one encoding pass without relying on source timestamps.</summary>
    private sealed class EncodingProgress : IProgress<double>
    {
        private readonly IProgress<double>? _progress;
        private readonly IProgress<VideoEncodingProgress> _statistics;
        private readonly Stopwatch _window = Stopwatch.StartNew();
        private readonly long? _totalFrames;
        private readonly bool _isTotalEstimated;
        private readonly int _pass;
        private readonly int _passCount;
        private long _windowFrames;
        private long _processedFrames;
        private double _fps;

        public EncodingProgress(IProgress<double>? progress, IProgress<VideoEncodingProgress> statistics,
            double durationSeconds, double outputFps, int pass, int passCount, long? exactTotalFrames = null)
        {
            _progress = progress;
            _statistics = statistics;
            _pass = pass;
            _passCount = passCount;
            double estimate = Math.Ceiling(durationSeconds * outputFps - 0.0000001);
            _totalFrames = exactTotalFrames is > 0 ? exactTotalFrames
                : double.IsFinite(estimate) && estimate > 0 && estimate < long.MaxValue ? (long)estimate : null;
            _isTotalEstimated = !(exactTotalFrames is > 0) && _totalFrames.HasValue;
            Publish(completed: false);
        }

        public void Report(double value) => _progress?.Report(value);

        public void ReportFrame()
        {
            _processedFrames++;
            _windowFrames++;
            if (_window.Elapsed.TotalSeconds >= 0.5) Flush();
        }

        public void Flush(bool completed = false)
        {
            double seconds = _window.Elapsed.TotalSeconds;
            if (_windowFrames > 0 && seconds > 0) _fps = _windowFrames / seconds;
            Publish(completed);
            _windowFrames = 0;
            _window.Restart();
        }

        private void Publish(bool completed) => _statistics.Report(new VideoEncodingProgress(_processedFrames,
            completed ? _processedFrames : _totalFrames is { } total ? Math.Max(total, _processedFrames) : null,
            _fps, !completed && (_isTotalEstimated || _totalFrames is { } expected && _processedFrames > expected), _pass, _passCount));
    }
}
