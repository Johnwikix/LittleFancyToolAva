using FancyToolAva.Models;
using FancyToolAva.Services;
using FFmpeg.AutoGen;

namespace FfmpegSmokeTest;

/// <summary>
/// 流复制（stream copy）烟测：走 VideoTranscodeService.TranscodeAsync 真实管线，
/// 覆盖双流复制（同容器 / 换容器）、视频/音频单侧混合、时间段裁剪（关键帧近似）、
/// 无音源、GIF 容器强制重编码、互斥守卫（滤镜 / 拼接）与容器不兼容预检。
/// </summary>
internal static partial class Program
{
    private static void RunCopyCases(VideoTranscodeService svc, string sample, string workDir)
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        string Out(string name) => Path.Combine(workDir, name);

        // 标准源：H.264 + AAC MP4（2s / 160x120 / 10fps，经真实转码路径产出；x264 ultrafast 默认 keyint=250 → 仅首帧关键帧）
        string src = Out("copy-src.mp4");
        RunExtra("copy: make h264+aac source", () =>
        {
            svc.TranscodeAsync(sample, src, new VideoTranscodeOptions
            {
                Container = VideoContainer.Mp4, VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.Aac,
                RateControl = RateControlMode.Crf, Crf = 32, Preset = PresetLevel.Ultrafast, AudioBitrateKbps = 64,
            }).GetAwaiter().GetResult();
            Verify(src, VideoCodec.H264, AudioCodec.Aac);
        });

        RunExtra("copy: both streams -> mp4 (remux)", () =>
        {
            string output = Out("copy-both.mp4");
            long srcBytes = new FileInfo(src).Length;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            svc.TranscodeAsync(src, output, CopyOptions(VideoContainer.Mp4, VideoCodec.Copy, AudioCodec.Copy)).GetAwaiter().GetResult();
            sw.Stop();
            var probe = svc.ProbeAsync(output).GetAwaiter().GetResult()!;
            AssertCopyResult(svc, probe, src, output);
            // remux 不应显著膨胀（封装开销 < 20%），且远快于重编码
            if (new FileInfo(output).Length > srcBytes * 1.2)
                throw new InvalidOperationException($"remux size inflated: {srcBytes} -> {new FileInfo(output).Length}");
            Console.WriteLine($"    remux {srcBytes} -> {new FileInfo(output).Length} bytes in {sw.ElapsedMilliseconds}ms");
        });

        RunExtra("copy: both streams -> mkv (container change)", () =>
        {
            string output = Out("copy-both.mkv");
            svc.TranscodeAsync(src, output, CopyOptions(VideoContainer.Mkv, VideoCodec.Copy, AudioCodec.Copy)).GetAwaiter().GetResult();
            var probe = svc.ProbeAsync(output).GetAwaiter().GetResult()!;
            if (probe.VideoCodec != "h264" || !probe.HasAudio || probe.AudioCodec != "aac")
                throw new InvalidOperationException($"mkv remux: got {probe.VideoCodec}/{(probe.HasAudio ? probe.AudioCodec : "none")}");
            AssertDuration(svc, output, 2000, 300);
        });

        RunExtra("copy: video copy + audio opus -> mkv", () =>
        {
            string output = Out("copy-v-audio-transcode.mkv");
            svc.TranscodeAsync(src, output, CopyOptions(VideoContainer.Mkv, VideoCodec.Copy, AudioCodec.Opus)).GetAwaiter().GetResult();
            var probe = svc.ProbeAsync(output).GetAwaiter().GetResult()!;
            if (probe.VideoCodec != "h264" || !probe.HasAudio || probe.AudioCodec != "opus")
                throw new InvalidOperationException($"mixed v-copy: got {probe.VideoCodec}/{(probe.HasAudio ? probe.AudioCodec : "none")}");
            AssertDuration(svc, output, 2000, 300);
        });

        RunExtra("copy: video h265 + audio copy -> mp4", () =>
        {
            string output = Out("copy-a-video-transcode.mp4");
            svc.TranscodeAsync(src, output, CopyOptions(VideoContainer.Mp4, VideoCodec.H265, AudioCodec.Copy)).GetAwaiter().GetResult();
            var probe = svc.ProbeAsync(output).GetAwaiter().GetResult()!;
            if (probe.VideoCodec != "hevc" || !probe.HasAudio || probe.AudioCodec != "aac")
                throw new InvalidOperationException($"mixed a-copy: got {probe.VideoCodec}/{(probe.HasAudio ? probe.AudioCodec : "none")}");
            AssertDuration(svc, output, 2000, 300);
        });

        RunExtra("copy: trim 0.5..1.5 keeps keyframe-aligned start", () =>
        {
            // 源只有首帧关键帧：起点近似回退到关键帧(0)，终点精确到 1.5s → 输出 ≈1.5s
            string output = Out("copy-trim.mp4");
            var options = CopyOptions(VideoContainer.Mp4, VideoCodec.Copy, AudioCodec.Copy);
            options.TrimStartSeconds = 0.5;
            options.TrimEndSeconds = 1.5;
            svc.TranscodeAsync(src, output, options).GetAwaiter().GetResult();
            var probe = svc.ProbeAsync(output).GetAwaiter().GetResult()!;
            if (probe.VideoCodec != "h264")
                throw new InvalidOperationException($"trim copy codec: {probe.VideoCodec}");
            AssertDuration(svc, output, 1500, 400);
        });

        RunExtra("copy: audio copy from silent source drops audio", () =>
        {
            string silent = Out("copy-silent-src.mp4");
            svc.TranscodeAsync(sample, silent, new VideoTranscodeOptions
            {
                Container = VideoContainer.Mp4, VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.None,
                RateControl = RateControlMode.Crf, Crf = 32, Preset = PresetLevel.Ultrafast,
            }).GetAwaiter().GetResult();
            string output = Out("copy-silent.mp4");
            svc.TranscodeAsync(silent, output, CopyOptions(VideoContainer.Mp4, VideoCodec.Copy, AudioCodec.Copy)).GetAwaiter().GetResult();
            var probe = svc.ProbeAsync(output).GetAwaiter().GetResult()!;
            if (probe.HasAudio) throw new InvalidOperationException("audio should be absent for silent source");
            if (probe.VideoCodec != "h264") throw new InvalidOperationException($"silent copy codec: {probe.VideoCodec}");
        });

        RunExtra("copy: gif container forces re-encode (Copy ignored)", () =>
        {
            // GIF 容器在服务端永远走 gif 编码，Copy 请求不生效也不报错
            string output = Out("copy-as.gif");
            var options = CopyOptions(VideoContainer.Gif, VideoCodec.Copy, AudioCodec.Copy);
            options.GifWidth = 120;
            options.GifFps = 8;
            svc.TranscodeAsync(src, output, options).GetAwaiter().GetResult();
            Verify(output, VideoCodec.Gif, AudioCodec.None);
        });

        RunExtra("copy guard: scale conflict rejected", () =>
        {
            var options = CopyOptions(VideoContainer.Mp4, VideoCodec.Copy, AudioCodec.Copy);
            options.ScaleMode = ScaleMode.Exact;
            options.ScaleWidth = 100;
            options.ScaleHeight = 40;
            ExpectCopyConflict(() => svc.TranscodeAsync(src, Out("copy-conflict.mp4"), options).GetAwaiter().GetResult());
        });

        RunExtra("copy guard: two-pass conflict rejected", () =>
        {
            // 两通对 copy 无意义：即便调用方强行开启（UI 已禁用），服务端入口也整体拒绝
            var options = CopyOptions(VideoContainer.Mp4, VideoCodec.Copy, AudioCodec.Copy);
            options.TwoPassEnabled = true;
            options.RateControl = RateControlMode.Bitrate;
            options.VideoBitrateKbps = 1000;
            ExpectCopyConflict(() => svc.TranscodeAsync(src, Out("copy-conflict-2pass.mp4"), options).GetAwaiter().GetResult());
        });

        RunExtra("copy guard: concat rejected", () =>
        {
            var options = CopyOptions(VideoContainer.Mp4, VideoCodec.Copy, AudioCodec.Aac);
            try
            {
                svc.ConcatTranscodeAsync([new VideoConcatSegment(src, 1, 0, null)], Out("copy-concat.mp4"), options).GetAwaiter().GetResult();
                throw new InvalidOperationException("concat + copy should be rejected");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Msg_CopyUnsupportedInConcat") || ex.Message.Contains("拼接模式不支持直接复制流") || ex.Message.Contains("Concat mode does not support"))
            {
                // 预期：Msg_CopyUnsupportedInConcat（烟测进程可能未加载语言资源，消息回退为键名）
            }
        });

        RunExtra("copy: vp8 -> mov container rejected", () =>
        {
            // VP8 无法写入 MOV：avformat_query_codec 预检或 muxer 写头（"VP8 muxing ... not supported"）任一防线拒绝
            string vp8Src = Out("copy-vp8-src.mkv");
            svc.TranscodeAsync(sample, vp8Src, new VideoTranscodeOptions
            {
                Container = VideoContainer.Mkv, VideoCodec = VideoCodec.Vp8, AudioCodec = AudioCodec.None,
                RateControl = RateControlMode.Crf, Crf = 40, Preset = PresetLevel.Ultrafast,
            }).GetAwaiter().GetResult();
            try
            {
                svc.TranscodeAsync(vp8Src, Out("copy-vp8.mov"), CopyOptions(VideoContainer.Mov, VideoCodec.Copy, AudioCodec.Copy)).GetAwaiter().GetResult();
                throw new InvalidOperationException("vp8 -> mov copy should be rejected");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Msg_CopyCodecUnsupported")
                || ex.Message.Contains("流复制失败") || ex.Message.Contains("Stream copy failed")
                || ex.Message.Contains("写入文件头失败") || ex.Message.Contains("muxing is currently not supported"))
            {
                Console.WriteLine($"    rejected: {ex.Message.Split('\n')[0]}");
            }
            if (File.Exists(Out("copy-vp8.mov"))) throw new InvalidOperationException("rejected copy must not leave output");
        });
    }

    private static VideoTranscodeOptions CopyOptions(VideoContainer container, VideoCodec v, AudioCodec a) => new()
    {
        Container = container, VideoCodec = v, AudioCodec = a,
    };

    /// <summary>断言双流复制后编码与源一致且时长完整。</summary>
    private static void AssertCopyResult(VideoTranscodeService svc, VideoProbeInfo probe, string srcPath, string outputPath)
    {
        var srcProbe = svc.ProbeAsync(srcPath).GetAwaiter().GetResult()!;
        if (probe.VideoCodec != srcProbe.VideoCodec)
            throw new InvalidOperationException($"remux video codec: want {srcProbe.VideoCodec} got {probe.VideoCodec}");
        if (probe.HasAudio != srcProbe.HasAudio || (srcProbe.HasAudio && probe.AudioCodec != srcProbe.AudioCodec))
            throw new InvalidOperationException($"remux audio codec: want {srcProbe.AudioCodec} got {(probe.HasAudio ? probe.AudioCodec : "none")}");
        AssertDuration(svc, outputPath, srcProbe.DurationMs, 300);
    }

    private static void ExpectCopyConflict(Action run)
    {
        try
        {
            run();
            throw new InvalidOperationException("conflicting options should be rejected");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Msg_CopyConflictingOptions") || ex.Message.Contains("直接复制不能与") || ex.Message.Contains("Direct copy cannot"))
        {
            // 预期：Msg_CopyConflictingOptions（烟测进程可能未加载语言资源，消息回退为键名）
        }
    }
}
