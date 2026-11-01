using FancyToolAva.Models;
using FancyToolAva.Services;
using FFmpeg.AutoGen;

namespace FfmpegSmokeTest;

/// <summary>
/// 拼接模式烟测：走 VideoTranscodeService.ConcatTranscodeAsync 真实管线，
/// 覆盖顺序拼接、循环、裁剪×循环、异构分辨率（letterbox 像素断言）、有音/无音段混合、
/// GIF、容器/编码器组合、两通、进度/取消与非法输入。
/// </summary>
internal static partial class Program
{
    private static void RunConcatCases(VideoTranscodeService svc, string sample, string workDir)
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        VideoConcatSegment Seg(string path, int loop = 1, double start = 0, double? end = null)
            => new(path, loop, start, end);
        string Out(string name) => Path.Combine(workDir, name);

        VideoTranscodeOptions O() => new()
        {
            Container = VideoContainer.Mp4, VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.Aac,
            RateControl = RateControlMode.Crf, Crf = 32, Preset = PresetLevel.Ultrafast, AudioBitrateKbps = 64,
        };

        // 异构段：不同分辨率/帧率/宽高比（16:9 vs 4:3 → letterbox 验证）
        string wide = Path.Combine(workDir, "concat-wide.avi");
        RunExtra("concat: make wide source", () => WriteTestAvi(wide, width: 320, height: 180, fps: 24, seconds: 2));
        // 无音轨段（经真实转码路径产出）
        string silent = Out("concat-silent.mp4");
        RunExtra("concat: make silent source", () => svc.TranscodeAsync(sample, silent, new VideoTranscodeOptions
        {
            Container = VideoContainer.Mp4, VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.None,
            Preset = PresetLevel.Ultrafast, Crf = 32,
        }).GetAwaiter().GetResult());

        RunExtra("concat 3 homogeneous segments", () =>
        {
            string output = Out("concat-3same.mp4");
            svc.ConcatTranscodeAsync([Seg(sample), Seg(sample), Seg(sample)], output, O()).GetAwaiter().GetResult();
            AssertDuration(svc, output, 6000, 350);
            Verify(output, VideoCodec.H264, AudioCodec.Aac);
        });

        RunExtra("concat single segment loop x3", () =>
        {
            string output = Out("concat-loop3.mp4");
            svc.ConcatTranscodeAsync([Seg(sample, loop: 3)], output, O()).GetAwaiter().GetResult();
            AssertDuration(svc, output, 6000, 350);
        });

        RunExtra("concat trim x loop", () =>
        {
            string output = Out("concat-trim-loop.mp4");
            svc.ConcatTranscodeAsync([Seg(sample, loop: 2, start: 0.5, end: 1.5)], output, O()).GetAwaiter().GetResult();
            AssertDuration(svc, output, 2000, 300);
        });

        RunExtra("concat heterogeneous letterbox + order", () =>
        {
            string output = Out("concat-hetero.mp4");
            svc.ConcatTranscodeAsync([Seg(sample), Seg(wide), Seg(sample, loop: 2)], output, O()).GetAwaiter().GetResult();
            var probe = svc.ProbeAsync(output).GetAwaiter().GetResult()!;
            if (probe.Width != 160 || probe.Height != 120)
                throw new InvalidOperationException($"canvas: want 160x120 got {probe.Width}x{probe.Height}");
            AssertDuration(svc, output, 8000, 500);
            // 第二段 320x180 内容等比缩到 160x90 居中：上下各 15px 黑边；黑边暗、画面亮
            var frame = svc.GetPreviewAsync(output, 3.0).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("preview missing at wide segment");
            if (frame.Width != 160 || frame.Height != 120) throw new InvalidOperationException($"preview size {frame.Width}x{frame.Height}");
            if (RowLuma(frame, 0) > 30 || RowLuma(frame, 5) > 30 || RowLuma(frame, 115) > 30 || RowLuma(frame, 119) > 30)
                throw new InvalidOperationException($"letterbox bars not black: y0={RowLuma(frame, 0):F0} y5={RowLuma(frame, 5):F0} y115={RowLuma(frame, 115):F0} y119={RowLuma(frame, 119):F0}");
            // 内容区（y 15..104）应有亮行（源是垂直渐变且存在暗行，取区间最大亮度判断内容确实落在画布内）
            double brightest = Enumerable.Range(15, 90).Max(r => RowLuma(frame, r));
            if (brightest < 120)
                throw new InvalidOperationException($"letterbox content missing: brightest row {brightest:F0}");
        });

        RunExtra("concat mixed audio presence", () =>
        {
            string output = Out("concat-mixed-audio.mp4");
            svc.ConcatTranscodeAsync([Seg(sample), Seg(silent)], output, O()).GetAwaiter().GetResult();
            AssertDuration(svc, output, 4000, 400);
            var audio = ReadAudioSamples(output);
            // 只有基准段（2s）出声；第二段静默区间不产生样本
            double expected = 2.0 * audio.Rate;
            if (Math.Abs(audio.Samples - expected) > 0.15 * audio.Rate)
                throw new InvalidOperationException($"audio samples: want ~{expected:F0} got {audio.Samples} @{audio.Rate}");
        });

        RunExtra("concat gif", () =>
        {
            string output = Out("concat.gif");
            svc.ConcatTranscodeAsync([Seg(sample), Seg(sample)], output, new VideoTranscodeOptions
            {
                Container = VideoContainer.Gif, VideoCodec = VideoCodec.Gif, AudioCodec = AudioCodec.None,
                GifWidth = 120, GifFps = 8, GifMaxColors = 64,
            }).GetAwaiter().GetResult();
            Verify(output, VideoCodec.Gif, AudioCodec.None);
            AssertDuration(svc, output, 4000, 400);
        });

        RunExtra("concat mkv vp9/opus", () =>
        {
            string output = Out("concat-vp9.mkv");
            svc.ConcatTranscodeAsync([Seg(sample), Seg(wide)], output, new VideoTranscodeOptions
            {
                Container = VideoContainer.Mkv, VideoCodec = VideoCodec.Vp9, AudioCodec = AudioCodec.Opus,
                RateControl = RateControlMode.Crf, Crf = 40, Preset = PresetLevel.Ultrafast, AudioBitrateKbps = 64,
            }).GetAwaiter().GetResult();
            Verify(output, VideoCodec.Vp9, AudioCodec.Opus);
        });

        RunExtra("concat nonzero-timestamp segment", () =>
        {
            string offset = Out("concat-offset-input.ts");
            string baseMp4 = Out("concat-offset-base.mp4");
            svc.TranscodeAsync(sample, baseMp4, O()).GetAwaiter().GetResult();
            WriteOffsetSource(baseMp4, offset);
            string output = Out("concat-offset.mp4");
            svc.ConcatTranscodeAsync([Seg(sample), Seg(offset)], output, O()).GetAwaiter().GetResult();
            AssertDuration(svc, output, 4000, 400);
        });

        RunExtra("concat 2-pass", () =>
        {
            string output = Out("concat-2pass.mp4");
            svc.ConcatTranscodeAsync([Seg(sample), Seg(sample)], output, new VideoTranscodeOptions
            {
                Container = VideoContainer.Mp4, VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.Aac,
                RateControl = RateControlMode.Bitrate, VideoBitrateKbps = 400, TwoPassEnabled = true,
                Preset = PresetLevel.Ultrafast, AudioBitrateKbps = 64,
            }).GetAwaiter().GetResult();
            AssertDuration(svc, output, 4000, 400);
        });

        RunExtra("concat progress and statistics", () =>
        {
            var progress = new List<double>();
            var statistics = new List<VideoEncodingProgress>();
            string output = Out("concat-progress.mp4");
            svc.ConcatTranscodeAsync([Seg(sample), Seg(silent, loop: 2)], output, O(),
                new CaptureProgress<double>(progress), statistics: new CaptureProgress<VideoEncodingProgress>(statistics))
                .GetAwaiter().GetResult();
            if (progress.Count == 0 || Math.Abs(progress[^1] - 1) > 0.001 || progress.Any(p => p < 0 || p > 1))
                throw new InvalidOperationException($"progress invalid: n={progress.Count} last={progress.LastOrDefault()}");
            for (int i = 1; i < progress.Count; i++)
                if (progress[i] < progress[i - 1] - 0.001) throw new InvalidOperationException("progress not monotonic");
            if (statistics.Count == 0 || statistics[^1].ProcessedFrames <= 0)
                throw new InvalidOperationException("statistics missing");
        });

        RunExtra("concat cancellation cleans temp", () =>
        {
            string output = Out("concat-canceled.mp4");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                svc.ConcatTranscodeAsync([Seg(sample), Seg(sample)], output, O(), ct: cancellation.Token)
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException("Canceled concat was accepted");
            }
            catch (OperationCanceledException) { }
            if (File.Exists(output)) throw new InvalidOperationException("output exists after cancellation");
            if (Directory.GetFiles(workDir, "*.tmp.*").Length > 0)
                throw new InvalidOperationException("temp file left behind: " + string.Join(", ", Directory.GetFiles(workDir, "*.tmp.*")));
        });

        RunExtra("concat invalid inputs", () =>
        {
            try
            {
                svc.ConcatTranscodeAsync([], Out("concat-invalid.mp4"), O()).GetAwaiter().GetResult();
                throw new InvalidOperationException("Empty segment list accepted");
            }
            catch (ArgumentException) { }
            try
            {
                svc.ConcatTranscodeAsync([Seg(sample)], Out("concat-invalid.mp4"), new VideoTranscodeOptions
                { SuperResolutionEnabled = true }).GetAwaiter().GetResult();
                throw new InvalidOperationException("Super-resolution option accepted");
            }
            catch (InvalidOperationException) { }
            try
            {
                svc.ConcatTranscodeAsync([Seg(Path.Combine(workDir, "missing-file.avi"))], Out("concat-invalid.mp4"), O())
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException("Missing file accepted");
            }
            catch (FileNotFoundException) { }
        });
    }

    private static double RowLuma(VideoPreviewFrame frame, int row)
    {
        int width = frame.Width;
        long sum = 0;
        for (int x = 0; x < width; x++)
        {
            int i = (row * width + x) * 4;
            // BGRA 粗略亮度
            sum += (frame.Pixels[i] * 11 + frame.Pixels[i + 1] * 59 + frame.Pixels[i + 2] * 30) / 100;
        }
        return (double)sum / width;
    }
}
