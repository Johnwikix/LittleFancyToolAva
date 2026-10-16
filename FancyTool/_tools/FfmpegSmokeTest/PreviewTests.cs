using System.Buffers;
using System.Diagnostics;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FancyToolAva.ViewModels;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static void RunPreviewCases(VideoTranscodeService service, string workDir)
    {
        RunExtra("exact container frame count is used only by count-preserving conversions", () =>
        {
            var options = new VideoTranscodeOptions();
            if (VideoTranscodeService.ExactOutputFrameCount(120, options) != 120
                || VideoTranscodeService.ExactOutputFrameCount(0, options) != null)
                throw new InvalidOperationException("Known and unknown source frame counts were confused");
            options.SuperResolutionEnabled = true;
            if (VideoTranscodeService.ExactOutputFrameCount(120, options) != 120)
                throw new InvalidOperationException("Spatial SR incorrectly discarded an exact frame count");
            options.InterpolationMode = VideoInterpolationMode.Rife;
            if (VideoTranscodeService.ExactOutputFrameCount(120, options) != null)
                throw new InvalidOperationException("Source frame count was presented as interpolated output count");
            options.InterpolationMode = VideoInterpolationMode.None;
            options.TrimEndSeconds = 1;
            if (VideoTranscodeService.ExactOutputFrameCount(120, options) != null)
                throw new InvalidOperationException("Source frame count was presented as a clipped output count");
            options.TrimEndSeconds = null;
            options.FpsMode = FpsMode.Fixed;
            if (VideoTranscodeService.ExactOutputFrameCount(120, options) != null)
                throw new InvalidOperationException("Source frame count was presented as a resampled output count");
        });
        RunExtra("bounded latest preview ownership", () =>
        {
            var pool = new PreviewTestPool();
            using var buffer = new VideoPreviewBuffer(pool);
            byte[] pixels = new byte[16];
            buffer.Publish(pixels, 2, 2, 1);
            using (var taken = buffer.TakeLatest()!)
            {
                for (int i = 2; i < 1000; i++) { pixels[0] = (byte)i; buffer.Publish(pixels, 2, 2, i); }
                using var latest = buffer.TakeLatest()!;
                if (taken.PositionSeconds != 1 || taken.Pixels[0] != 0 || latest.PositionSeconds != 999
                    || latest.Pixels[0] != (byte)(999 % 256) || buffer.TakeLatest() != null || pool.Peak > 3)
                    throw new InvalidOperationException("Preview ownership, freshness, or memory bound failed");
            }
            buffer.Publish(pixels, 2, 2, 1000);
            buffer.Dispose();
            buffer.Publish(pixels, 2, 2, 1001);
            if (pool.Outstanding != 0 || buffer.TakeLatest() != null) throw new InvalidOperationException("Preview buffers leaked after disposal");
        });
        RunExtra("live preview cadence and final frame", () =>
        {
            string input = Path.Combine(workDir, "preview-60fps.avi");
            WriteTestAvi(input, 160, 120, 60, 2);
            if (service.ProbeAsync(input).GetAwaiter().GetResult()?.FrameCount != 120)
                throw new InvalidOperationException("FFmpeg's exact AVI source frame count was not retained");
            var preview = new PacedPreview();
            var processingRates = new List<VideoEncodingProgress>();
            var elapsed = Stopwatch.StartNew();
            service.TranscodeAsync(input, Path.Combine(workDir, "preview-60fps.mp4"), new VideoTranscodeOptions
            { Preset = PresetLevel.Ultrafast }, preview: preview, statistics: new CaptureProgress<VideoEncodingProgress>(processingRates)).GetAwaiter().GetResult();
            if (preview.Count < 100 || Math.Abs(preview.LastPosition - 119d / 60) > 0.002)
                throw new InvalidOperationException($"Preview cadence/final frame: {preview.Count} frames, last={preview.LastPosition}");
            if (processingRates.Count < 2 || !processingRates.Any(rate => rate.FramesPerSecond > 0)
                || processingRates.Any(rate => !double.IsFinite(rate.FramesPerSecond) || rate.FramesPerSecond < 0)
                || processingRates[0] is not { TotalFrames: 120, IsTotalEstimated: false }
                || processingRates[^1] is not { ProcessedFrames: 120, TotalFrames: 120, IsTotalEstimated: false })
                throw new InvalidOperationException("Conversion did not report valid measured processing FPS");
            Console.WriteLine($"    producer: {preview.Count} previews in {elapsed.Elapsed.TotalSeconds:F2}s ({preview.Count / elapsed.Elapsed.TotalSeconds:F1} fps)");
            // Encoding faster than the display cap must still publish the ending position.
            var burst = new LastPreview();
            service.TranscodeAsync(input, Path.Combine(workDir, "preview-burst.mp4"), new VideoTranscodeOptions
            { Preset = PresetLevel.Ultrafast }, preview: burst).GetAwaiter().GetResult();
            if (burst.Last == null || Math.Abs(burst.Last.PositionSeconds - 119d / 60) > 0.002)
                throw new InvalidOperationException("Fast conversion omitted the final preview");
        });
    }

    private static void CheckLivePreviewUi(VideoTranscodeViewModel vm, VideoFileItem selected, VideoFileItem other)
    {
        var sink = (IVideoPreviewSink)vm.CreateProgressPreview(selected);
        var ignored = (IVideoPreviewSink)vm.CreateProgressPreview(other);
        if (!sink.WantsFrame || ignored.WantsFrame) throw new InvalidOperationException("Preview did not filter the selected job");
        vm.IsBusy = true;
        int rendered = 0;
        object? bitmap = null;
        Exception? error = null;
        void Updated()
        {
            rendered++;
            if (bitmap != null && !ReferenceEquals(bitmap, vm.PreviewBitmap)) error = new InvalidOperationException("UI allocated another bitmap for same-size preview");
            bitmap = vm.PreviewBitmap;
        }
        vm.PreviewFrameUpdated += Updated;
        var elapsed = Stopwatch.StartNew();
        var producer = Task.Run(async () =>
        {
            byte[] pixels = new byte[160 * 120 * 4];
            for (int i = 0; i < 120; i++)
            {
                pixels[0] = (byte)i;
                sink.Publish(pixels, 160, 120, i / 60d);
                await Task.Delay(17);
            }
            for (int i = 0; i < 500; i++) sink.Publish(pixels, 160, 120, 2 + i);
        });
        PumpUntil(() => producer.IsCompleted && vm.PreviewPositionSeconds == 501);
        producer.GetAwaiter().GetResult();
        vm.PreviewFrameUpdated -= Updated;
        if (error != null) throw error;
        if (rendered < 60) throw new InvalidOperationException($"Live UI too slow: {rendered} updates in {elapsed.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"Live preview UI: {rendered} updates in {elapsed.Elapsed.TotalSeconds:F2}s ({rendered / elapsed.Elapsed.TotalSeconds:F1} fps), bitmap reused; burst displayed latest frame");
        vm.SelectedFileItem = other;
        if (sink.WantsFrame || !ignored.WantsFrame) throw new InvalidOperationException("Selection change retained the old preview producer");
        vm.SetPreviewVisible(false);
        if (ignored.WantsFrame) throw new InvalidOperationException("Hidden preview still requests conversion work");
        vm.SetPreviewVisible(true);
        vm.SelectedFileItem = selected;
        vm.IsBusy = false;
    }

    private sealed class PacedPreview : IProgress<VideoPreviewFrame>
    {
        internal int Count;
        internal double LastPosition;
        public void Report(VideoPreviewFrame value) { Count++; LastPosition = value.PositionSeconds; Thread.Sleep(20); }
    }

    private sealed class LastPreview : IProgress<VideoPreviewFrame>
    {
        internal VideoPreviewFrame? Last;
        public void Report(VideoPreviewFrame value) => Last = value;
    }

    private sealed class PreviewTestPool : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> _outstanding = [];
        internal int Outstanding => _outstanding.Count;
        internal int Peak;
        public override byte[] Rent(int minimumLength)
        {
            var array = new byte[minimumLength];
            _outstanding.Add(array);
            Peak = Math.Max(Peak, _outstanding.Count);
            return array;
        }
        public override void Return(byte[] array, bool clearArray = false)
        {
            if (!_outstanding.Remove(array)) throw new InvalidOperationException("Preview buffer returned twice");
        }
    }
}
