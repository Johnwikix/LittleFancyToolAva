using System.Text;
using FFmpeg.AutoGen;
using FancyToolAva.Models;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

/// <summary>
/// 进程内视频转码（参考 music_player FFmpegAudioConverter 的直调 DLL 管线）：
/// avformat 解复用 → avcodec 解码 → avfilter 滤镜图（crop/去隔行/降噪/缩放/fps/像素格式，
/// GIF 走 palettegen/paletteuse 复杂图）→ avcodec 编码（软编 libx264/x265/aom/svtav1/vpx，
/// 硬编 nvenc/qsv/amf/vaapi）→ avformat 封装。音频经 swresample 重采样（可选增益）后编码。
/// 无 ffmpeg.exe 子进程，无外部 ffprobe；滤镜串与旧 CLI 参数一一对应。
/// </summary>
public sealed unsafe class VideoTranscodeService : IVideoTranscodeService
{
    private readonly ILogger<VideoTranscodeService> _logger;

    private const int ProbeTimeoutSec = 15;
    private static readonly int[] OpusRates = [8000, 12000, 16000, 24000, 48000];
    // AVERROR(EAGAIN)：EAGAIN 在 Windows(FFmpeg 兼容层) 与 POSIX 均为 11，AutoGen 未导出常量
    private const int ErrEagain = -11;

    public VideoTranscodeService(ILogger<VideoTranscodeService> logger)
    {
        _logger = logger;
    }

    // ══════════════════════════════ 探测 ══════════════════════════════

    // Root the native callback; its opaque deadline lives until avformat_close_input returns.
    private static readonly AVIOInterruptCB_callback ProbeInterrupt = static opaque =>
        System.Diagnostics.Stopwatch.GetTimestamp() >= *(long*)opaque ? 1 : 0;

    private sealed record SourceInfo(VideoProbeInfo Probe, string? PixFmt);

    public Task<VideoProbeInfo?> ProbeAsync(string inputPath, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException(inputPath);
        return Task.Run<VideoProbeInfo?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            FFmpegNative.TryLoad(out _);
            return ProbeInternal(inputPath).Probe;
        }, ct);
    }

    /// <summary>打开容器读取流信息：时长/分辨率/帧率/编解码器/码率/容器 + 源像素格式。</summary>
    private static SourceInfo ProbeInternal(string input)
    {
        AVFormatContext* fmt = null;
        AVDictionary* options = null;
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + ProbeTimeoutSec * System.Diagnostics.Stopwatch.Frequency;
        try
        {
            fmt = ffmpeg.avformat_alloc_context();
            if (fmt == null) throw new InvalidOperationException("avformat_alloc_context failed");
            fmt->interrupt_callback = new AVIOInterruptCB { callback = ProbeInterrupt, opaque = &deadline };
            fmt->probesize = 5 * 1024 * 1024;
            fmt->max_analyze_duration = 5 * ffmpeg.AV_TIME_BASE;
            ffmpeg.av_dict_set(&options, "protocol_whitelist", "file", 0);

            int ret = ffmpeg.avformat_open_input(&fmt, input, null, &options);
            if (ret < 0 || fmt == null)
                throw FFmpegNative.CreateException(ret, string.Format(LocalizationRegistry.Get("VideoTranscode.Msg_OpenFailed")!, input));
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                throw new InvalidOperationException(string.Format(LocalizationRegistry.Get("VideoTranscode.Msg_ReadStreamInfoFailed")!, input));

            long durationMs = fmt->duration > 0 ? (long)(fmt->duration * 1000.0 / ffmpeg.AV_TIME_BASE) : 0;
            long bitRate = fmt->bit_rate > 0 ? fmt->bit_rate : 0;
            string container = PtrToString(fmt->iformat->name).Split(',')[0];

            int width = 0, height = 0;
            double fps = 0;
            string vCodec = "", aCodec = "";
            string? pixFmt = null;
            bool hasVideo = false, hasAudio = false;

            for (uint i = 0; i < fmt->nb_streams; i++)
            {
                AVStream* s = fmt->streams[i];
                AVCodecParameters* par = s->codecpar;
                if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && !hasVideo)
                {
                    hasVideo = true;
                    vCodec = ffmpeg.avcodec_get_name(par->codec_id);
                    width = par->width;
                    height = par->height;
                    AVRational fr = s->avg_frame_rate;
                    if (fr.num <= 0 || fr.den <= 0) fr = s->r_frame_rate;
                    if (fr.num > 0 && fr.den > 0) fps = ffmpeg.av_q2d(fr);
                    if (par->format >= 0)
                    {
                        string? n = ffmpeg.av_get_pix_fmt_name((AVPixelFormat)par->format);
                        if (!string.IsNullOrEmpty(n)) pixFmt = n;
                    }
                }
                else if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && !hasAudio)
                {
                    hasAudio = true;
                    aCodec = ffmpeg.avcodec_get_name(par->codec_id);
                }
            }

            var probe = new VideoProbeInfo(input, durationMs, width, height, fps, vCodec, aCodec, container, bitRate, hasAudio, hasVideo);
            return new SourceInfo(probe, pixFmt);
        }
        finally
        {
            if (options != null) ffmpeg.av_dict_free(&options);
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        }
    }

    internal static string PtrToString(byte* p)
    {
        if (p == null) return "";
        int len = 0;
        while (p[len] != 0) len++;
        return len == 0 ? "" : Encoding.ASCII.GetString(p, len);
    }

    // ══════════════════════════════ 转码入口 ══════════════════════════════

    public Task TranscodeAsync(string inputPath, string outputPath, VideoTranscodeOptions options,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException(inputPath);
        return Task.Run(() =>
        {
            if (!FFmpegNative.TryLoad(out Exception? loadError))
                throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegLoadFailed", loadError!.Message));
            ct.ThrowIfCancellationRequested();

            var src = ProbeInternal(inputPath);
            long durationMs = src.Probe.DurationMs;

            string tmpPath = BuildTmpPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            string encoderName = MapVideoEncoderName(options);
            bool twoPass = options.TwoPassEnabled
                && options.RateControl == RateControlMode.Bitrate
                && options.Container != VideoContainer.Gif
                && options.HardwareBackend == HardwareBackend.Software
                && encoderName is "libx264" or "libx265";

            try
            {
                if (twoPass)
                {
                    string passLog = Path.Combine(Path.GetTempPath(), $"ffmpeg2pass_{Guid.NewGuid():N}");
                    string pass1Out = Path.Combine(Path.GetTempPath(), $"fancytool-pass1-{Guid.NewGuid():N}{Path.GetExtension(outputPath)}");
                    try
                    {
                        _logger.LogInformation("FFmpeg in-process 2-pass pass1: {Input} encoder={Enc}", inputPath, encoderName);
                        ConvertOnce(inputPath, pass1Out, options, src.PixFmt, src.Probe.Fps, pass: 1, passLogFile: passLog,
                            durationMs, new ScaledProgress(progress, 0, 0.5), ct);
                        _logger.LogInformation("FFmpeg in-process 2-pass pass2: {Input} -> {Output}", inputPath, outputPath);
                        ConvertOnce(inputPath, tmpPath, options, src.PixFmt, src.Probe.Fps, pass: 2, passLogFile: passLog,
                            durationMs, new ScaledProgress(progress, 0.5, 0.5), ct);
                    }
                    finally
                    {
                        TryDelete(pass1Out);
                        TryDelete(passLog);
                        TryDelete(passLog + "-0.log");
                        TryDelete(passLog + ".temp");
                        TryDelete(passLog + "-0.log.mbtree");
                        TryDelete(passLog + "-0.log.tmp");
                    }
                }
                else
                {
                    _logger.LogInformation("FFmpeg in-process transcode: {Input} -> {Output} encoder={Enc} muxer={Fmt}",
                        inputPath, outputPath, encoderName, GetMuxerName(options.Container));
                    ConvertOnce(inputPath, tmpPath, options, src.PixFmt, src.Probe.Fps, pass: 0, passLogFile: null, durationMs, progress, ct);
                }

                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tmpPath, outputPath);
                progress?.Report(1.0);
                _logger.LogInformation("Transcode done: {Input} -> {Output}", inputPath, outputPath);
            }
            catch
            {
                TryDelete(tmpPath);
                throw;
            }
        }, ct);
    }

    /// <summary>单次完整管线执行（两通编码时 pass=1/2，pass=0 表示单通）。</summary>
    private void ConvertOnce(string inputPath, string outputPath, VideoTranscodeOptions o, string? sourcePixFmt,
        double sourceFps, int pass, string? passLogFile, long durationMs, IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        AVFormatContext* inFmt = null;
        AVCodecContext* vDec = null;
        AVCodecContext* aDec = null;
        AVFormatContext* ofmt = null;
        AVCodecContext* vEnc = null;
        AVCodecContext* aEnc = null;
        AVFilterGraph* graph = null;
        AVFilterContext* srcCtx = null;
        AVFilterContext* sinkCtx = null;
        AVBufferRef* hwDevice = null;
        AVBufferRef* hwFrames = null;
        SwrContext* swr = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        AVPacket* encPkt = ffmpeg.av_packet_alloc();
        AVFrame* decFrame = ffmpeg.av_frame_alloc();
        AVFrame* filtFrame = ffmpeg.av_frame_alloc();
        AVFrame* swrFrame = ffmpeg.av_frame_alloc();
        EncoderFrameFeeder? audioFeeder = null;
        AVStream* vOutStream = null;
        AVStream* aOutStream = null;
        bool outputOpened = false;

        try
        {
            // ── 1. 输入 ──
            AVDictionary* openOpts = null;
            ffmpeg.av_dict_set(&openOpts, "protocol_whitelist", "file", 0);
            int ret = ffmpeg.avformat_open_input(&inFmt, inputPath, null, &openOpts);
            ffmpeg.av_dict_free(&openOpts);
            if (ret < 0) throw FFmpegNative.CreateException(ret, $"无法打开输入文件: {inputPath}");
            if (ffmpeg.avformat_find_stream_info(inFmt, null) < 0)
                throw new InvalidOperationException($"无法读取流信息: {inputPath}");

            int vIdx = ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (vIdx < 0) throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_NoVideoStream"));
            AVStream* vInStream = inFmt->streams[vIdx];

            bool wantAudio = o.Container != VideoContainer.Gif && o.AudioCodec != AudioCodec.None;
            int aIdx = wantAudio
                ? ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0)
                : -1;

            vDec = OpenDecoder(vInStream);
            if (aIdx >= 0)
            {
                aDec = OpenDecoder(inFmt->streams[aIdx]);
                // WAV/PCM 解码器常给出 UNSPEC 顺序布局；规范化成默认布局，swr 配置比较才不会报 INPUT_CHANGED
                if (aDec->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
                {
                    int channels = Math.Max(1, aDec->ch_layout.nb_channels);
                    ffmpeg.av_channel_layout_uninit(&aDec->ch_layout);
                    ffmpeg.av_channel_layout_default(&aDec->ch_layout, channels);
                }
            }

            // ── 2. 输出封装 ──
            ffmpeg.avformat_alloc_output_context2(&ofmt, null, GetMuxerName(o.Container), outputPath);
            if (ofmt == null) throw new InvalidOperationException($"不支持的输出容器: {o.Container}");
            if (o.Container == VideoContainer.Gif)
                TrySetOpt(ofmt->priv_data, "loop", o.GifLoop <= 0 ? "0" : o.GifLoop.ToString());

            // ── 3. 视频编码器查询 + 像素格式对齐（需先于滤镜图：format 滤镜与编码器能力必须一致）──
            string encoderName = MapVideoEncoderName(o);
            AVCodec* vCodecPtr = ffmpeg.avcodec_find_encoder_by_name(encoderName);
            if (vCodecPtr == null)
                throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_EncoderUnavailable", encoderName));
            string? targetPixFmt = null;
            if (o.Container != VideoContainer.Gif && !IsVaapiBackend(o.HardwareBackend))
                targetPixFmt = AlignTargetPixFmt(vCodecPtr, o, sourcePixFmt);

            // ── 3b. 视频滤镜图（缓冲源参数来自解码器上下文）──
            graph = ffmpeg.avfilter_graph_alloc();
            srcCtx = CreateBufferSource(graph, vDec, vInStream);
            sinkCtx = CreateBufferSink(graph);

            string filterDesc = BuildFilterDescription(o, targetPixFmt);
            if (!BindGraphAndConfigure(graph, srcCtx, sinkCtx, filterDesc,
                    IsVaapiBackend(o.HardwareBackend), "/dev/dri/renderD128", &hwDevice))
                throw new InvalidOperationException($"滤镜图初始化失败: {filterDesc}");

            // hwupload 之后 sink 协商出 hw 帧格式：为 vaapi 编码器派生同源 hw_frames_ctx
            if (hwDevice != null)
            {
                AVFilterLink* sinkLink = sinkCtx->inputs[0];
                hwFrames = ffmpeg.av_hwframe_ctx_alloc(hwDevice);
                AVHWFramesContext* fc = (AVHWFramesContext*)hwFrames->data;
                fc->format = (AVPixelFormat)sinkLink->format;
                fc->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
                fc->width = sinkLink->w;
                fc->height = sinkLink->h;
                if (ffmpeg.av_hwframe_ctx_init(hwFrames) < 0)
                    throw new InvalidOperationException("VAAPI 硬件帧池初始化失败");
            }

            // ── 4. 视频编码器 ──
            vEnc = ffmpeg.avcodec_alloc_context3(vCodecPtr);
            ConfigureVideoEncoder(vEnc, o, pass, passLogFile,
                sinkCtx->inputs[0], hwDevice != null ? hwFrames : null, ofmt, sourceFps, targetPixFmt);
            ret = ffmpeg.avcodec_open2(vEnc, vCodecPtr, null);
            if (ret < 0) throw FFmpegNative.CreateException(ret, $"视频编码器打开失败 ({encoderName})");

            vOutStream = ffmpeg.avformat_new_stream(ofmt, null);
            vOutStream->time_base = vEnc->time_base;
            ffmpeg.avcodec_parameters_from_context(vOutStream->codecpar, vEnc);

            // ── 5. 音频编码器 ──
            if (aDec != null)
            {
                aEnc = SetupAudioEncoder(ofmt, aDec, o, out aOutStream, out swr);
                audioFeeder = new EncoderFrameFeeder(aEnc, ofmt, aOutStream, encPkt);
            }

            // ── 6. 打开输出、写文件头 ──
            if ((ofmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ret = ffmpeg.avio_open(&ofmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE);
                if (ret < 0) throw FFmpegNative.CreateException(ret, $"无法创建输出文件: {outputPath}");
                outputOpened = true;
            }
            ret = ffmpeg.avformat_write_header(ofmt, null);
            if (ret < 0) throw FFmpegNative.CreateException(ret, "写入文件头失败");

            // ── 7. 主循环：解复用 → 解码 → 滤镜 → 编码 → 封装 ──
            double totalSec = durationMs > 0 ? durationMs / 1000.0 : 0;
            // 无 pts 的解码帧按源帧率推算（帧时间戳统一用输入流 time_base，直通解码器）
            double srcFps = ffmpeg.av_q2d(vInStream->avg_frame_rate);
            if (srcFps <= 0) srcFps = 30;
            double streamTbSec = ffmpeg.av_q2d(vInStream->time_base);
            long frameStep = Math.Max(1, (long)Math.Round((1.0 / srcFps) / Math.Max(1e-9, streamTbSec)));
            long nextPts = 0;

            while ((ret = ffmpeg.av_read_frame(inFmt, pkt)) >= 0)
            {
                ct.ThrowIfCancellationRequested();
                if (pkt->stream_index == vIdx)
                {
                    ret = ffmpeg.avcodec_send_packet(vDec, pkt);
                    if (ret == ErrEagain)
                    {
                        // 解码器输出满：先排空再重送一次（数据包不得丢弃）
                        ProcessVideoFrames(vDec, decFrame, filtFrame, srcCtx, sinkCtx, vEnc, ofmt, vOutStream, encPkt, totalSec, progress, ref nextPts, frameStep);
                        ret = ffmpeg.avcodec_send_packet(vDec, pkt);
                    }
                    if (ret < 0 && ret != ErrEagain && ret != ffmpeg.AVERROR_EOF)
                        throw FFmpegNative.CreateException(ret, "视频解码失败");
                    ffmpeg.av_packet_unref(pkt);
                    ProcessVideoFrames(vDec, decFrame, filtFrame, srcCtx, sinkCtx, vEnc, ofmt, vOutStream, encPkt, totalSec, progress, ref nextPts, frameStep);
                }
                else if (aDec != null && pkt->stream_index == aIdx)
                {
                    ret = ffmpeg.avcodec_send_packet(aDec, pkt);
                    if (ret == ErrEagain)
                    {
                        ProcessAudioFrames(aDec, decFrame, swrFrame, aEnc, swr, audioFeeder!, o.AudioGainDb);
                        ret = ffmpeg.avcodec_send_packet(aDec, pkt);
                    }
                    if (ret < 0 && ret != ErrEagain && ret != ffmpeg.AVERROR_EOF)
                        throw FFmpegNative.CreateException(ret, "音频解码失败");
                    ffmpeg.av_packet_unref(pkt);
                    ProcessAudioFrames(aDec, decFrame, swrFrame, aEnc, swr, audioFeeder!, o.AudioGainDb);
                }
                else
                {
                    ffmpeg.av_packet_unref(pkt);
                }
            }
            CheckReadError(ret, inputPath);

            // ── 8. 冲洗：解码器 → 滤镜图 → 音频 → 编码器 ──
            ffmpeg.avcodec_send_packet(vDec, null);
            ProcessVideoFrames(vDec, decFrame, filtFrame, srcCtx, sinkCtx, vEnc, ofmt, vOutStream, encPkt, totalSec, progress, ref nextPts, frameStep);

            ffmpeg.av_buffersrc_add_frame(srcCtx, null); // 通知 EOF，滤镜图内部冲刷
            DrainSink(sinkCtx, filtFrame, vEnc, ofmt, vOutStream, encPkt, totalSec, progress);
            ffmpeg.avcodec_send_frame(vEnc, null);
            DrainPackets(vEnc, ofmt, vOutStream, encPkt, vEnc->time_base);

            if (aDec != null)
            {
                ffmpeg.avcodec_send_packet(aDec, null);
                ProcessAudioFrames(aDec, decFrame, swrFrame, aEnc, swr, audioFeeder!, o.AudioGainDb);
                FlushSwr(swr, swrFrame, aEnc, audioFeeder!, o.AudioGainDb);
                audioFeeder!.Flush();
                ffmpeg.avcodec_send_frame(aEnc, null);
                DrainPackets(aEnc, ofmt, aOutStream!, encPkt, aEnc->time_base);
            }

            ffmpeg.av_write_trailer(ofmt);
        }
        finally
        {
            audioFeeder?.Dispose();
            ffmpeg.av_frame_free(&swrFrame);
            ffmpeg.av_frame_free(&filtFrame);
            ffmpeg.av_frame_free(&decFrame);
            ffmpeg.av_packet_free(&encPkt);
            ffmpeg.av_packet_free(&pkt);
            if (swr != null) ffmpeg.swr_free(&swr);
            if (hwFrames != null) ffmpeg.av_buffer_unref(&hwFrames);
            if (hwDevice != null) ffmpeg.av_buffer_unref(&hwDevice);
            if (graph != null) ffmpeg.avfilter_graph_free(&graph);
            if (aEnc != null) ffmpeg.avcodec_free_context(&aEnc);
            if (vEnc != null) ffmpeg.avcodec_free_context(&vEnc);
            if (ofmt != null)
            {
                if (outputOpened && ofmt->pb != null && (ofmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                    ffmpeg.avio_closep(&ofmt->pb);
                ffmpeg.avformat_free_context(ofmt);
            }
            if (aDec != null) ffmpeg.avcodec_free_context(&aDec);
            if (vDec != null) ffmpeg.avcodec_free_context(&vDec);
            if (inFmt != null) ffmpeg.avformat_close_input(&inFmt);
        }
    }

    private static void CheckReadError(int ret, string input)
    {
        // av_read_frame 循环退出：0 = EOF 正常；其余为真实 IO 错误
        if (ret < 0 && ret != ffmpeg.AVERROR_EOF && ret != ErrEagain)
            throw FFmpegNative.CreateException(ret, $"读取输入失败: {input}");
    }

    private static AVCodecContext* OpenDecoder(AVStream* stream)
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null)
            throw new InvalidOperationException($"没有可用解码器: {ffmpeg.avcodec_get_name(stream->codecpar->codec_id)}");
        AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(ctx, stream->codecpar);
        ctx->thread_count = 0;
        if (ffmpeg.avcodec_open2(ctx, codec, null) < 0)
            throw new InvalidOperationException("解码器打开失败");
        return ctx;
    }

    // ── 帧/包流动 ──

    /// <summary>排空解码器输出帧并推送进滤镜图 → 编码 → 封装；无 pts 的帧按 frameStep 推算。</summary>
    private static void ProcessVideoFrames(AVCodecContext* vDec, AVFrame* decFrame, AVFrame* filtFrame,
        AVFilterContext* srcCtx, AVFilterContext* sinkCtx, AVCodecContext* vEnc,
        AVFormatContext* ofmt, AVStream* stream, AVPacket* pkt,
        double totalSec, IProgress<double>? progress, ref long nextPts, long frameStep)
    {
        while (ffmpeg.avcodec_receive_frame(vDec, decFrame) >= 0)
        {
            if (decFrame->pts == ffmpeg.AV_NOPTS_VALUE)
            {
                decFrame->pts = nextPts;
                nextPts += frameStep;
            }
            else
            {
                nextPts = decFrame->pts + frameStep;
            }
            PushVideoFrame(srcCtx, sinkCtx, decFrame, filtFrame, vEnc, ofmt, stream, pkt, totalSec, progress);
            ffmpeg.av_frame_unref(decFrame);
        }
    }

    private static void PushVideoFrame(AVFilterContext* srcCtx, AVFilterContext* sinkCtx, AVFrame* decFrame,
        AVFrame* filtFrame, AVCodecContext* vEnc, AVFormatContext* ofmt, AVStream* stream, AVPacket* pkt,
        double totalSec, IProgress<double>? progress)
    {
        int ret = ffmpeg.av_buffersrc_add_frame(srcCtx, decFrame);
        if (ret < 0 && ret != ErrEagain)
            throw FFmpegNative.CreateException(ret, "滤镜图送帧失败");
        // 每帧送入后立即排空 sink（滤镜内部缓冲有限）
        DrainSinkInto(sinkCtx, filtFrame, vEnc, ofmt, stream, pkt, totalSec, progress);
    }

    private static void DrainSink(AVFilterContext* sinkCtx, AVFrame* filtFrame, AVCodecContext* vEnc,
        AVFormatContext* ofmt, AVStream* stream, AVPacket* pkt, double totalSec, IProgress<double>? progress)
        => DrainSinkInto(sinkCtx, filtFrame, vEnc, ofmt, stream, pkt, totalSec, progress);

    private static void DrainSinkInto(AVFilterContext* sinkCtx, AVFrame* filtFrame, AVCodecContext* vEnc,
        AVFormatContext* ofmt, AVStream* stream, AVPacket* pkt, double totalSec, IProgress<double>? progress)
    {
        AVRational sinkTb = sinkCtx->inputs[0]->time_base;
        while (ffmpeg.av_buffersink_get_frame(sinkCtx, filtFrame) >= 0)
        {
            filtFrame->pts = ffmpeg.av_rescale_q(filtFrame->pts, sinkTb, vEnc->time_base);
            int ret = ffmpeg.avcodec_send_frame(vEnc, filtFrame);
            if (ret < 0) throw FFmpegNative.CreateException(ret, "视频编码失败");
            if (totalSec > 0 && filtFrame->pts != ffmpeg.AV_NOPTS_VALUE)
                progress?.Report(Math.Clamp(ffmpeg.av_q2d(vEnc->time_base) * filtFrame->pts, 0, totalSec) / totalSec);
            ffmpeg.av_frame_unref(filtFrame);
            DrainPackets(vEnc, ofmt, stream, pkt, vEnc->time_base);
        }
    }

    private static void ProcessAudioFrames(AVCodecContext* aDec, AVFrame* decFrame, AVFrame* swrFrame,
        AVCodecContext* aEnc, SwrContext* swr, EncoderFrameFeeder feeder, double gainDb)
    {
        while (ffmpeg.avcodec_receive_frame(aDec, decFrame) >= 0)
        {
            ConvertAudioFrame(decFrame, swrFrame, aEnc, swr, feeder, gainDb);
            ffmpeg.av_frame_unref(decFrame);
        }
    }

    private static void DrainPackets(AVCodecContext* enc, AVFormatContext* ofmt, AVStream* stream,
        AVPacket* pkt, AVRational encTb)
    {
        while (ffmpeg.avcodec_receive_packet(enc, pkt) >= 0)
        {
            ffmpeg.av_packet_rescale_ts(pkt, encTb, stream->time_base);
            pkt->stream_index = stream->index;
            ffmpeg.av_interleaved_write_frame(ofmt, pkt);
            ffmpeg.av_packet_unref(pkt);
        }
    }

    // ── 滤镜图搭建 ──

    private static AVFilterContext* CreateBufferSource(AVFilterGraph* graph, AVCodecContext* dec, AVStream* stream)
    {
        AVFilter* srcFilter = ffmpeg.avfilter_get_by_name("buffer");
        if (srcFilter == null) throw new InvalidOperationException("avfilter 缺少 buffer 源");
        AVFilterContext* ctx = ffmpeg.avfilter_graph_alloc_filter(graph, srcFilter, "in");
        if (ctx == null) throw new InvalidOperationException("buffer 源分配失败");

        AVRational tb = stream->time_base;
        if (tb.num <= 0 || tb.den <= 0) tb = new AVRational { num = 1, den = 90000 };
        AVRational fr = stream->avg_frame_rate;
        if (fr.num <= 0 || fr.den <= 0) fr = new AVRational { num = 0, den = 1 };
        AVRational sar = dec->sample_aspect_ratio;
        if (sar.num <= 0 || sar.den <= 0) sar = new AVRational { num = 1, den = 1 };

        string args = $"video_size={Math.Max(dec->width, 1)}x{Math.Max(dec->height, 1)}:" +
                      $"pix_fmt={(int)dec->pix_fmt}:" +
                      $"time_base={tb.num}/{tb.den}:pixel_aspect={sar.num}/{sar.den}:" +
                      $"frame_rate={fr.num}/{fr.den}";
        if (ffmpeg.avfilter_init_str(ctx, args) < 0)
            throw new InvalidOperationException("buffer 源初始化失败");
        return ctx;
    }

    private static AVFilterContext* CreateBufferSink(AVFilterGraph* graph)
    {
        AVFilter* sinkFilter = ffmpeg.avfilter_get_by_name("buffersink");
        if (sinkFilter == null) throw new InvalidOperationException("avfilter 缺少 buffersink");
        AVFilterContext* ctx = ffmpeg.avfilter_graph_alloc_filter(graph, sinkFilter, "out");
        if (ctx == null) throw new InvalidOperationException("buffersink 分配失败");
        if (ffmpeg.avfilter_init_str(ctx, null) < 0)
            throw new InvalidOperationException("buffersink 初始化失败");
        return ctx;
    }

    /// <summary>
    /// 把 buffersrc/buffersink 绑定到描述字符串两端并配置滤镜图（ffmpeg filtering 官方示例的
    /// 绑定方式：inout 节点 name 用隐式标签 "in"/"out" 匹配无标签端点）；
    /// vaapi 时创建硬件设备并挂到 hwupload。返回 false 表示配置失败。
    /// </summary>
    private static bool BindGraphAndConfigure(AVFilterGraph* graph, AVFilterContext* src, AVFilterContext* sink,
        string description, bool vaapi, string vaapiDevice, AVBufferRef** outHwDevice)
    {
        // 命名与官方示例一致：outputs = 图的开放输入（← buffersrc），inputs = 开放输出（→ buffersink）
        AVFilterInOut* outputs = ffmpeg.avfilter_inout_alloc();
        AVFilterInOut* inputs = ffmpeg.avfilter_inout_alloc();
        if (outputs == null || inputs == null)
        {
            if (outputs != null) ffmpeg.avfilter_inout_free(&outputs);
            if (inputs != null) ffmpeg.avfilter_inout_free(&inputs);
            return false;
        }
        try
        {
            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = src;
            outputs->pad_idx = 0;
            outputs->next = null;
            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = sink;
            inputs->pad_idx = 0;
            inputs->next = null;

            int ret = ffmpeg.avfilter_graph_parse_ptr(graph, description, &inputs, &outputs, null);
            if (ret < 0) return false;

            if (vaapi)
            {
                AVBufferRef* dev = null;
                ret = ffmpeg.av_hwdevice_ctx_create(&dev, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, vaapiDevice, null, 0);
                if (ret < 0 || dev == null) return false;
                *outHwDevice = dev;
                for (uint i = 0; i < graph->nb_filters; i++)
                {
                    AVFilterContext* f = graph->filters[i];
                    if (PtrToString(f->filter->name) == "hwupload" && f->hw_device_ctx == null)
                        f->hw_device_ctx = ffmpeg.av_buffer_ref(dev);
                }
            }

            return ffmpeg.avfilter_graph_config(graph, null) >= 0;
        }
        finally
        {
            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);
        }
    }

    // ── 编码器配置 ──

    private static void ConfigureVideoEncoder(AVCodecContext* enc, VideoTranscodeOptions o,
        int pass, string? passLogFile, AVFilterLink* sinkLink, AVBufferRef* hwFrames, AVFormatContext* ofmt,
        double sourceFps, string? targetPixFmt)
    {
        string encoderName = MapVideoEncoderName(o);
        bool vaapi = IsVaapiBackend(o.HardwareBackend);

        enc->thread_count = 0;
        enc->width = sinkLink->w;
        enc->height = sinkLink->h;
        enc->sample_aspect_ratio = sinkLink->sample_aspect_ratio;
        if (enc->sample_aspect_ratio.num == 0 || enc->sample_aspect_ratio.den == 0)
            enc->sample_aspect_ratio = new AVRational { num = 1, den = 1 };

        double fps;
        if (o.Container == VideoContainer.Gif)
            fps = o.GifFps > 0 ? o.GifFps : 15;
        else if (o.FpsMode != FpsMode.SameAsSource)
            fps = o.FpsValue;
        else
            fps = sourceFps > 0 ? sourceFps : 30;
        enc->framerate = ffmpeg.av_d2q(fps, 4096);
        enc->time_base = new AVRational { num = enc->framerate.den, den = enc->framerate.num };

        if (vaapi)
        {
            enc->pix_fmt = AVPixelFormat.AV_PIX_FMT_VAAPI;
            enc->hw_frames_ctx = ffmpeg.av_buffer_ref(hwFrames!);
        }
        else if (o.Container == VideoContainer.Gif)
        {
            enc->pix_fmt = AVPixelFormat.AV_PIX_FMT_PAL8;
        }
        else
        {
            enc->pix_fmt = targetPixFmt != null
                ? ffmpeg.av_get_pix_fmt(targetPixFmt)
                : (AVPixelFormat)sinkLink->format;
        }

        if ((ofmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            enc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        if (o.RateControl == RateControlMode.Crf)
            ApplyQualityMode(enc, o, encoderName);
        else
            ApplyBitrateMode(enc, o, encoderName);

        ApplyEncoderPreset(enc, o, encoderName);
        if (!string.IsNullOrEmpty(o.Profile)) TrySetOpt(enc->priv_data, "profile", o.Profile);
        if (!string.IsNullOrEmpty(o.Level))
        {
            if (!TrySetOpt(enc->priv_data, "level", o.Level))
                TrySetOpt(enc->priv_data, "level_idc", o.Level);
        }
        if (encoderName is "libvpx" or "libvpx-vp9")
        {
            TrySetOpt(enc->priv_data, "auto-alt-ref", "1");
            TrySetOpt(enc->priv_data, "lag-in-frames", "25");
        }

        // 两通：libx264/libx265 自行读写 stats 文件（CLI -pass/-passlogfile 的等价物）
        if (pass == 1)
        {
            enc->flags |= ffmpeg.AV_CODEC_FLAG_PASS1;
            _ = passLogFile != null && TrySetOpt(enc->priv_data, "stats", passLogFile);
        }
        else if (pass == 2)
        {
            enc->flags |= ffmpeg.AV_CODEC_FLAG_PASS2;
            if (passLogFile != null && !TrySetOpt(enc->priv_data, "stats", passLogFile))
                _ = TrySetOpt(enc->priv_data, "stats", passLogFile + "-0.log");
        }
    }

    /// <summary>CRF/质量模式：各编码器对应私有小项（等价 CLI -crf / -qp / -global_quality）。</summary>
    private static void ApplyQualityMode(AVCodecContext* enc, VideoTranscodeOptions o, string encoderName)
    {
        string crf = o.Crf.ToString(System.Globalization.CultureInfo.InvariantCulture);
        switch (encoderName)
        {
            case "libx264":
            case "libx265":
            case "libsvtav1":
            case "libaom-av1":
            case "libvpx":
            case "libvpx-vp9":
                TrySetOpt(enc->priv_data, "crf", crf);
                break;
            case "mpeg4":
                enc->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
                enc->global_quality = ffmpeg.FF_QP2LAMBDA * Math.Clamp(o.Crf / 5, 1, 31);
                break;
            case "h264_nvenc":
            case "hevc_nvenc":
            case "av1_nvenc":
                TrySetOpt(enc->priv_data, "rc", "constqp");
                TrySetOpt(enc->priv_data, "constqp", crf);
                break;
            case "h264_qsv":
            case "hevc_qsv":
            case "av1_qsv":
                TrySetOpt(enc->priv_data, "global_quality", crf);
                break;
            case "h264_amf":
            case "hevc_amf":
            case "av1_amf":
                TrySetOpt(enc->priv_data, "qp_i", crf);
                TrySetOpt(enc->priv_data, "qp_p", crf);
                break;
            default:
                if (encoderName.EndsWith("_vaapi", StringComparison.Ordinal))
                {
                    enc->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
                    enc->global_quality = o.Crf;
                }
                break;
        }
    }

    private static void ApplyBitrateMode(AVCodecContext* enc, VideoTranscodeOptions o, string encoderName)
    {
        enc->bit_rate = Math.Max(16, o.VideoBitrateKbps) * 1000L;
        if (o.VideoCodec is VideoCodec.H264 or VideoCodec.H265)
        {
            enc->rc_max_rate = enc->bit_rate;
            enc->rc_buffer_size = (int)Math.Min(int.MaxValue, enc->bit_rate * 2);
        }
        // aom/svt 码率模式下避免遗留 crf 私有项（新 ctx 本就无值，占位保持结构清晰）
        _ = encoderName;
    }

    private static void ApplyEncoderPreset(AVCodecContext* enc, VideoTranscodeOptions o, string encoderName)
    {
        switch (encoderName)
        {
            case "libx264":
            case "libx265":
                TrySetOpt(enc->priv_data, "preset", PresetName(o.Preset));
                break;
            case "libsvtav1":
                // svt 的 preset 是速度档数字（越大越快）
                TrySetOpt(enc->priv_data, "preset", o.Preset switch
                {
                    PresetLevel.Ultrafast => "10",
                    PresetLevel.Superfast => "9",
                    PresetLevel.Veryfast => "8",
                    PresetLevel.Faster => "7",
                    PresetLevel.Fast => "6",
                    PresetLevel.Medium => "5",
                    PresetLevel.Slow => "4",
                    PresetLevel.Slower => "3",
                    PresetLevel.Veryslow => "2",
                    _ => "1"
                });
                break;
            case "libaom-av1":
                TrySetOpt(enc->priv_data, "cpu-used", o.Preset switch
                {
                    PresetLevel.Ultrafast or PresetLevel.Superfast => "8",
                    PresetLevel.Veryfast => "7",
                    PresetLevel.Faster => "6",
                    PresetLevel.Fast => "5",
                    PresetLevel.Medium => "4",
                    PresetLevel.Slow => "3",
                    PresetLevel.Slower => "2",
                    _ => "1"
                });
                TrySetOpt(enc->priv_data, "usage", "goodq");
                break;
            case "h264_nvenc":
            case "hevc_nvenc":
            case "av1_nvenc":
                TrySetOpt(enc->priv_data, "preset", o.Preset switch
                {
                    PresetLevel.Ultrafast => "p1",
                    PresetLevel.Superfast => "p2",
                    PresetLevel.Veryfast => "p3",
                    PresetLevel.Faster or PresetLevel.Fast => "p4",
                    PresetLevel.Medium => "p5",
                    PresetLevel.Slow or PresetLevel.Slower => "p6",
                    _ => "p7"
                });
                break;
            case "h264_qsv":
            case "hevc_qsv":
            case "av1_qsv":
                TrySetOpt(enc->priv_data, "preset", o.Preset switch
                {
                    PresetLevel.Ultrafast or PresetLevel.Superfast or PresetLevel.Veryfast => "veryfast",
                    PresetLevel.Faster or PresetLevel.Fast => "fast",
                    PresetLevel.Medium => "medium",
                    _ => "slow"
                });
                break;
            case "h264_amf":
            case "hevc_amf":
            case "av1_amf":
                TrySetOpt(enc->priv_data, "quality", o.Preset switch
                {
                    PresetLevel.Ultrafast or PresetLevel.Superfast or PresetLevel.Veryfast => "speed",
                    PresetLevel.Faster or PresetLevel.Fast or PresetLevel.Medium => "balanced",
                    _ => "quality"
                });
                break;
        }
    }

    private static string PresetName(PresetLevel p) => p switch
    {
        PresetLevel.Ultrafast => "ultrafast",
        PresetLevel.Superfast => "superfast",
        PresetLevel.Veryfast => "veryfast",
        PresetLevel.Faster => "faster",
        PresetLevel.Fast => "fast",
        PresetLevel.Medium => "medium",
        PresetLevel.Slow => "slow",
        PresetLevel.Slower => "slower",
        PresetLevel.Veryslow => "veryslow",
        PresetLevel.Placebo => "placebo",
        _ => "medium"
    };

    private static bool TrySetOpt(void* obj, string name, string value)
        => obj != null && ffmpeg.av_opt_set(obj, name, value, 0) >= 0;

    // ── 音频管线（参考 FFmpegAudioConverter：swr 重采样 + 定长供帧器）──

    private static AVCodecContext* SetupAudioEncoder(AVFormatContext* ofmt, AVCodecContext* aDec, VideoTranscodeOptions o,
        out AVStream* outStream, out SwrContext* swr)
    {
        (AVCodecID codecId, AVSampleFormat fmt) map = o.AudioCodec switch
        {
            AudioCodec.Aac => (AVCodecID.AV_CODEC_ID_AAC, AVSampleFormat.AV_SAMPLE_FMT_FLTP),
            AudioCodec.Mp3 => (AVCodecID.AV_CODEC_ID_MP3, AVSampleFormat.AV_SAMPLE_FMT_S32P),
            AudioCodec.Opus => (AVCodecID.AV_CODEC_ID_OPUS, AVSampleFormat.AV_SAMPLE_FMT_FLTP),
            AudioCodec.Vorbis => (AVCodecID.AV_CODEC_ID_VORBIS, AVSampleFormat.AV_SAMPLE_FMT_FLTP),
            AudioCodec.Flac => (AVCodecID.AV_CODEC_ID_FLAC, AVSampleFormat.AV_SAMPLE_FMT_S32),
            AudioCodec.Ac3 => (AVCodecID.AV_CODEC_ID_AC3, AVSampleFormat.AV_SAMPLE_FMT_FLTP),
            _ => (AVCodecID.AV_CODEC_ID_AAC, AVSampleFormat.AV_SAMPLE_FMT_FLTP)
        };

        int inRate = aDec->sample_rate != 0 ? aDec->sample_rate : 48000;
        int outRate = inRate;
        if (map.codecId == AVCodecID.AV_CODEC_ID_OPUS)
        {
            // Opus 只工作在 8/12/16/24/48 kHz
            int best = OpusRates[0];
            foreach (int r in OpusRates)
                if (Math.Abs(r - outRate) < Math.Abs(best - outRate)) best = r;
            outRate = best;
        }

        AVCodec* codec = ffmpeg.avcodec_find_encoder(map.codecId);
        if (codec == null) throw new InvalidOperationException($"没有可用音频编码器: {map.codecId}");
        AVCodecContext* enc = ffmpeg.avcodec_alloc_context3(codec);
        enc->sample_fmt = map.fmt;
        enc->sample_rate = outRate;
        enc->bit_rate = map.codecId == AVCodecID.AV_CODEC_ID_FLAC
            ? 0 // 无损按样本编码，码率项无意义
            : Math.Max(16, o.AudioBitrateKbps) * 1000L;
        ffmpeg.av_channel_layout_copy(&enc->ch_layout, &aDec->ch_layout);
        // lame 等编码器只接受原生声道布局；统一规范化并在需要时下混
        ffmpeg.av_channel_layout_uninit(&enc->ch_layout);
        ffmpeg.av_channel_layout_default(&enc->ch_layout, Math.Max(1, enc->ch_layout.nb_channels));
        SelectEncoderChannelLayout(enc, codec);
        if (map.codecId == AVCodecID.AV_CODEC_ID_FLAC)
        {
            int srcDepth = aDec->bits_per_raw_sample > 0 ? aDec->bits_per_raw_sample : 16;
            if (srcDepth < 16) srcDepth = 16;
            enc->bits_per_raw_sample = srcDepth >= 24 ? 24 : 16;
            enc->compression_level = 8;
        }
        if ((ofmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            enc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        int ret = ffmpeg.avcodec_open2(enc, codec, null);
        if (ret < 0) throw FFmpegNative.CreateException(ret, $"音频编码器打开失败 ({map.codecId}, {outRate} Hz, {enc->ch_layout.nb_channels} 声道)");

        outStream = ffmpeg.avformat_new_stream(ofmt, null);
        outStream->time_base = new AVRational { num = 1, den = enc->sample_rate };
        ffmpeg.avcodec_parameters_from_context(outStream->codecpar, enc);

        SwrContext* swrCtx = null;
        ffmpeg.swr_alloc_set_opts2(&swrCtx,
            &enc->ch_layout, enc->sample_fmt, enc->sample_rate,
            &aDec->ch_layout, aDec->sample_fmt, aDec->sample_rate, 0, null);
        if (swrCtx == null || ffmpeg.swr_init(swrCtx) < 0)
            throw new InvalidOperationException("音频重采样器初始化失败");
        swr = swrCtx;
        return enc;
    }

    // 优先同声道数布局；不支持多声道的编码器（如 MP3）回退立体声/单声道，由 swr 实际下混
    private static void SelectEncoderChannelLayout(AVCodecContext* enc, AVCodec* encoder)
    {
        void* config = null;
        int count = 0;
        int ret = ffmpeg.avcodec_get_supported_config(enc, encoder,
            AVCodecConfig.AV_CODEC_CONFIG_CHANNEL_LAYOUT, 0, &config, &count);
        if (ret < 0 || config == null || count <= 0) return;
        AVChannelLayout* layouts = (AVChannelLayout*)config;
        AVChannelLayout* selected = null;
        int channels = enc->ch_layout.nb_channels;
        for (int i = 0; i < count; i++)
        {
            if (ffmpeg.av_channel_layout_compare(&enc->ch_layout, &layouts[i]) == 0) return;
            int candidateChannels = layouts[i].nb_channels;
            if (candidateChannels == channels ||
                (candidateChannels <= Math.Min(2, channels) &&
                 (selected == null || candidateChannels > selected->nb_channels)))
                selected = &layouts[i];
        }
        if (selected == null) throw new InvalidOperationException($"音频编码器不支持 {channels} 声道且没有兼容的下混布局");
        ffmpeg.av_channel_layout_uninit(&enc->ch_layout);
        ret = ffmpeg.av_channel_layout_copy(&enc->ch_layout, selected);
        if (ret < 0) throw FFmpegNative.CreateException(ret, "设置音频编码器声道布局失败");
    }

    private static void ConvertAudioFrame(AVFrame* decFrame, AVFrame* swrFrame, AVCodecContext* aEnc, SwrContext* swr,
        EncoderFrameFeeder feeder, double gainDb)
    {
        ffmpeg.av_frame_unref(swrFrame);
        swrFrame->format = (int)aEnc->sample_fmt;
        swrFrame->sample_rate = aEnc->sample_rate;
        ffmpeg.av_channel_layout_copy(&swrFrame->ch_layout, &aEnc->ch_layout);
        int ret = ffmpeg.swr_convert_frame(swr, swrFrame, decFrame);
        if (ret < 0) throw FFmpegNative.CreateException(ret, "音频重采样失败");
        if (swrFrame->nb_samples == 0) return;
        if (Math.Abs(gainDb) > 0.01) ApplyGain(swrFrame, Math.Pow(10, gainDb / 20.0));
        feeder.Push(swrFrame);
    }

    private static void FlushSwr(SwrContext* swr, AVFrame* swrFrame, AVCodecContext* aEnc,
        EncoderFrameFeeder feeder, double gainDb)
    {
        while (true)
        {
            ffmpeg.av_frame_unref(swrFrame);
            swrFrame->format = (int)aEnc->sample_fmt;
            swrFrame->sample_rate = aEnc->sample_rate;
            ffmpeg.av_channel_layout_copy(&swrFrame->ch_layout, &aEnc->ch_layout);
            int ret = ffmpeg.swr_convert_frame(swr, swrFrame, null);
            if (ret < 0 || swrFrame->nb_samples == 0) break;
            if (Math.Abs(gainDb) > 0.01) ApplyGain(swrFrame, Math.Pow(10, gainDb / 20.0));
            feeder.Push(swrFrame);
            if (ffmpeg.swr_get_out_samples(swr, 0) <= 0) break;
        }
    }

    /// <summary>浮点/整型采样域线性增益（整型域做钳位）。</summary>
    private static void ApplyGain(AVFrame* frame, double linear)
    {
        int channels = frame->ch_layout.nb_channels;
        switch ((AVSampleFormat)frame->format)
        {
            case AVSampleFormat.AV_SAMPLE_FMT_FLT:
            {
                float* p = (float*)frame->data[0];
                int n = frame->nb_samples * channels;
                for (int i = 0; i < n; i++) p[i] = (float)(p[i] * linear);
                break;
            }
            case AVSampleFormat.AV_SAMPLE_FMT_FLTP:
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    float* p = (float*)frame->data[(uint)ch];
                    for (int i = 0; i < frame->nb_samples; i++) p[i] = (float)(p[i] * linear);
                }
                break;
            }
            case AVSampleFormat.AV_SAMPLE_FMT_S32:
            case AVSampleFormat.AV_SAMPLE_FMT_S32P:
            {
                bool planar = (AVSampleFormat)frame->format == AVSampleFormat.AV_SAMPLE_FMT_S32P;
                int planes = planar ? channels : 1;
                int perPlane = planar ? frame->nb_samples : frame->nb_samples * channels;
                for (int p = 0; p < planes; p++)
                {
                    int* d = (int*)frame->data[(uint)p];
                    for (int i = 0; i < perPlane; i++)
                    {
                        double v = d[i] * linear;
                        d[i] = (int)Math.Clamp(v, int.MinValue, int.MaxValue);
                    }
                }
                break;
            }
            case AVSampleFormat.AV_SAMPLE_FMT_S16:
            case AVSampleFormat.AV_SAMPLE_FMT_S16P:
            {
                bool planar = (AVSampleFormat)frame->format == AVSampleFormat.AV_SAMPLE_FMT_S16P;
                int planes = planar ? channels : 1;
                int perPlane = planar ? frame->nb_samples : frame->nb_samples * channels;
                for (int p = 0; p < planes; p++)
                {
                    short* d = (short*)frame->data[(uint)p];
                    for (int i = 0; i < perPlane; i++)
                    {
                        double v = d[i] * linear;
                        d[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
                    }
                }
                break;
            }
        }
    }

    /// <summary>固定 frame_size 音频编码器（如 libopus 960 采样）的分帧供料器，每帧送编后立即取包写出。</summary>
    private sealed unsafe class EncoderFrameFeeder : IDisposable
    {
        private readonly AVCodecContext* _encCtx;
        private readonly AVFormatContext* _ofmt;
        private readonly AVStream* _stream;
        private readonly AVPacket* _pkt;
        private readonly AVFrame* _frame;
        private readonly bool _planar;
        private readonly int _bytesPerSample;
        private readonly int _channels;
        private readonly int _capacity;
        private int _filled;
        private long _nextPts;

        public EncoderFrameFeeder(AVCodecContext* encCtx, AVFormatContext* ofmt, AVStream* stream, AVPacket* pkt)
        {
            _encCtx = encCtx;
            _ofmt = ofmt;
            _stream = stream;
            _pkt = pkt;
            _capacity = encCtx->frame_size > 0 ? encCtx->frame_size : 0;
            _bytesPerSample = ffmpeg.av_get_bytes_per_sample(encCtx->sample_fmt);
            _channels = encCtx->ch_layout.nb_channels;
            _planar = ffmpeg.av_sample_fmt_is_planar(encCtx->sample_fmt) != 0;

            _frame = ffmpeg.av_frame_alloc();
            _frame->format = (int)encCtx->sample_fmt;
            _frame->sample_rate = encCtx->sample_rate;
            ffmpeg.av_channel_layout_copy(&_frame->ch_layout, &encCtx->ch_layout);
            _frame->nb_samples = _capacity > 0 ? _capacity : 1152;
            if (ffmpeg.av_frame_get_buffer(_frame, 0) < 0)
                throw new InvalidOperationException("音频编码帧缓冲分配失败");
        }

        public void Push(AVFrame* src)
        {
            if (_capacity == 0)
            {
                // 编码器接受任意帧长（lame/vorbis/pcm/flac），直通
                src->pts = _nextPts;
                _nextPts += src->nb_samples;
                Send(src);
                return;
            }
            int srcOffset = 0;
            while (srcOffset < src->nb_samples)
            {
                int copy = Math.Min(src->nb_samples - srcOffset, _capacity - _filled);
                CopySamples(src, srcOffset, _filled, copy);
                _filled += copy;
                srcOffset += copy;
                if (_filled == _capacity)
                {
                    _frame->nb_samples = _capacity;
                    _frame->pts = _nextPts;
                    _nextPts += _capacity;
                    Send(_frame);
                    ReallocFrame();
                }
            }
        }

        public void Flush()
        {
            if (_capacity > 0 && _filled > 0)
            {
                _frame->nb_samples = _filled;
                _frame->pts = _nextPts;
                Send(_frame);
                ReallocFrame();
            }
        }

        private void Send(AVFrame* frame)
        {
            int ret = ffmpeg.avcodec_send_frame(_encCtx, frame);
            if (ret < 0) throw FFmpegNative.CreateException(ret, "音频编码失败");
            DrainPackets(_encCtx, _ofmt, _stream, _pkt, _encCtx->time_base);
        }

        private void ReallocFrame()
        {
            ffmpeg.av_frame_unref(_frame);
            _frame->format = (int)_encCtx->sample_fmt;
            _frame->sample_rate = _encCtx->sample_rate;
            ffmpeg.av_channel_layout_copy(&_frame->ch_layout, &_encCtx->ch_layout);
            _frame->nb_samples = _capacity > 0 ? _capacity : 1152;
            if (ffmpeg.av_frame_get_buffer(_frame, 0) < 0)
                throw new InvalidOperationException("音频编码帧缓冲分配失败");
            _filled = 0;
        }

        private void CopySamples(AVFrame* src, int srcStart, int dstStart, int count)
        {
            if (_planar)
            {
                for (int ch = 0; ch < _channels; ch++)
                {
                    Buffer.MemoryCopy(
                        src->extended_data[ch] + (long)srcStart * _bytesPerSample,
                        _frame->data[(uint)ch] + (long)dstStart * _bytesPerSample,
                        (long)count * _bytesPerSample,
                        (long)count * _bytesPerSample);
                }
            }
            else
            {
                Buffer.MemoryCopy(
                    src->extended_data[0] + (long)srcStart * _channels * _bytesPerSample,
                    _frame->data[0] + (long)dstStart * _channels * _bytesPerSample,
                    (long)count * _channels * _bytesPerSample,
                    (long)count * _channels * _bytesPerSample);
            }
        }

        public void Dispose()
        {
            AVFrame* f = _frame;
            ffmpeg.av_frame_free(&f);
        }
    }

    /// <summary>
    /// 目标像素格式对齐编码器支持表：444 透传目标在 mpeg4/SVT-AV1/VP8 或显式 baseline/main/high
    /// profile 下不可用，逐级回落（想要格式 → yuv420p → 支持表首项）。
    /// </summary>
    private static string? AlignTargetPixFmt(AVCodec* codec, VideoTranscodeOptions o, string? sourcePixFmt)
    {
        string? want = PickTargetPixFmt(o, sourcePixFmt);
        if (want == null) return null;
        AVPixelFormat wantFmt = ffmpeg.av_get_pix_fmt(want);

        void* cfg = null;
        int cnt = 0;
        int ret = ffmpeg.avcodec_get_supported_config(null, codec, AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT, 0, &cfg, &cnt);
        if (ret < 0 || cfg == null || cnt <= 0) return want;
        AVPixelFormat* fmts = (AVPixelFormat*)cfg;

        bool hasWant = false, has420 = false;
        for (int i = 0; i < cnt; i++)
        {
            if (fmts[i] == wantFmt) hasWant = true;
            if (fmts[i] == AVPixelFormat.AV_PIX_FMT_YUV420P) has420 = true;
        }
        // baseline/main/high 等显式 profile 仅支持 420（422/444/10bit 需专门 profile 名），
        // 须在命中想要格式之前生效：444 源 + "high" 会让 x264 直接拒绝
        string prof = o.Profile ?? "";
        bool restrict420 = prof.Length > 0
            && !prof.Contains("422", StringComparison.OrdinalIgnoreCase)
            && !prof.Contains("444", StringComparison.OrdinalIgnoreCase)
            && !prof.Contains("10", StringComparison.Ordinal);
        if (restrict420 && has420) return "yuv420p";

        if (hasWant) return want;
        if (has420) return "yuv420p";
        string? n = ffmpeg.av_get_pix_fmt_name(fmts[0]);
        return string.IsNullOrEmpty(n) ? want : n;
    }

    // ══════════════════════════════ 滤镜串（与旧 CLI 参数构造等价）══════════════════════════════

    private static string BuildFilterDescription(VideoTranscodeOptions o, string? targetPixFmt)
    {
        if (o.Container == VideoContainer.Gif)
        {
            string fps = (o.GifFps > 0 ? o.GifFps : 15).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string width = (o.GifWidth > 0 ? o.GifWidth : 480).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var parts = new List<string> { $"fps={fps}", $"scale={width}:-1:flags=lanczos" };
            if (o.CropEnabled) parts.Add($"crop=iw-{o.CropLeft + o.CropRight}:ih-{o.CropTop + o.CropBottom}:{o.CropLeft}:{o.CropTop}");
            if (o.Deinterlace != DeinterlaceMode.None) parts.Add(o.Deinterlace == DeinterlaceMode.Bwdif ? "bwdif" : "yadif");
            if (o.Denoise != DenoiseMode.None) parts.Add(GetDenoiseFilter(o.Denoise));
            string dither = o.GifDither switch
            {
                GifDither.None => "none",
                GifDither.Bayer => "bayer:bayer_scale=5",
                GifDither.FloydSteinberg => "floyd_steinberg",
                GifDither.Sierpinski => "sierra2_4a",
                _ => "bayer:bayer_scale=5"
            };
            string stats = o.GifStatsMode == "single" ? "single" : "diff";
            string baseFilters = string.Join(",", parts);
            return $"{baseFilters},split[s0][s1];[s0]palettegen=max_colors={o.GifMaxColors}:stats_mode={stats}[p];[s1][p]paletteuse=dither={dither}";
        }

        var filters = new List<string>();
        if (o.CropEnabled)
            filters.Add($"crop=iw-{o.CropLeft + o.CropRight}:ih-{o.CropTop + o.CropBottom}:{o.CropLeft}:{o.CropTop}");
        if (o.Deinterlace != DeinterlaceMode.None)
            filters.Add(o.Deinterlace == DeinterlaceMode.Bwdif ? "bwdif" : "yadif");
        if (o.Denoise != DenoiseMode.None)
            filters.Add(GetDenoiseFilter(o.Denoise));
        if (o.ScaleMode != ScaleMode.None)
            filters.Add(BuildScaleFilter(o));
        if (o.FpsMode != FpsMode.SameAsSource)
            filters.Add($"fps={o.FpsValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        if (IsVaapiBackend(o.HardwareBackend))
        {
            filters.Add("format=nv12");
            filters.Add("hwupload");
        }
        else
        {
            if (targetPixFmt != null) filters.Add($"format={targetPixFmt}");
        }

        return filters.Count > 0 ? string.Join(",", filters) : "null";
    }

    private static string BuildScaleFilter(VideoTranscodeOptions o)
    {
        return o.ScaleMode switch
        {
            // force_divisible_by=2：等比缩到奇数高度会让 yuv420p 编码器打开失败（100x75 → x264 报错）
            ScaleMode.FitWithin => $"scale=w={o.ScaleWidth}:h={o.ScaleHeight}:force_original_aspect_ratio=decrease:force_divisible_by=2:eval=frame:flags=lanczos",
            ScaleMode.Exact => $"scale={o.ScaleWidth}:{o.ScaleHeight}:flags=lanczos",
            ScaleMode.Width => $"scale={o.ScaleWidth}:-2:flags=lanczos",
            ScaleMode.Height => $"scale=-2:{o.ScaleHeight}:flags=lanczos",
            _ => $"scale={o.ScaleWidth}:{o.ScaleHeight}:flags=lanczos"
        };
    }

    private static string GetDenoiseFilter(DenoiseMode m) => m switch
    {
        DenoiseMode.Hqdn3dLight => "hqdn3d=4:3:6:4.5",
        DenoiseMode.Hqdn3dMedium => "hqdn3d=8:6:8:6",
        DenoiseMode.Hqdn3dStrong => "hqdn3d=12:8:12:8",
        _ => "hqdn3d"
    };

    // ══════════════════════════════ 编码器/封装器映射 ══════════════════════════════

    private static string GetMuxerName(VideoContainer c) => c switch
    {
        VideoContainer.Mp4 => "mp4",
        VideoContainer.Mkv => "matroska",
        VideoContainer.Mov => "mov",
        VideoContainer.Avi => "avi",
        VideoContainer.Gif => "gif",
        _ => "mp4"
    };

    internal static bool IsVaapiBackend(HardwareBackend hw)
        => (hw == HardwareBackend.Intel || hw == HardwareBackend.Amd) && !OperatingSystem.IsWindows();

    /// <summary>与旧 MapVideoCodec/MapHardwareEncoder 相同的映射，产出 avcodec_find_encoder_by_name 名称。</summary>
    internal static string MapVideoEncoderName(VideoTranscodeOptions o)
    {
        if (o.Container == VideoContainer.Gif) return "gif";
        if (o.HardwareBackend != HardwareBackend.Software)
        {
            bool vaapi = IsVaapiBackend(o.HardwareBackend);
            string? hw = (o.VideoCodec, o.HardwareBackend, vaapi) switch
            {
                (VideoCodec.H264, HardwareBackend.Nvidia, _) => "h264_nvenc",
                (VideoCodec.H265, HardwareBackend.Nvidia, _) => "hevc_nvenc",
                (VideoCodec.Av1Aom, HardwareBackend.Nvidia, _) => "av1_nvenc",
                (VideoCodec.Av1Svt, HardwareBackend.Nvidia, _) => "av1_nvenc",
                (VideoCodec.H264, HardwareBackend.Intel, false) => "h264_qsv",
                (VideoCodec.H265, HardwareBackend.Intel, false) => "hevc_qsv",
                (VideoCodec.Av1Aom, HardwareBackend.Intel, false) => "av1_qsv",
                (VideoCodec.Av1Svt, HardwareBackend.Intel, false) => "av1_qsv",
                (VideoCodec.H264, HardwareBackend.Amd, false) => "h264_amf",
                (VideoCodec.H265, HardwareBackend.Amd, false) => "hevc_amf",
                (VideoCodec.Av1Aom, HardwareBackend.Amd, false) => "av1_amf",
                (VideoCodec.Av1Svt, HardwareBackend.Amd, false) => "av1_amf",
                (VideoCodec.H264, _, true) => "h264_vaapi",
                (VideoCodec.H265, _, true) => "hevc_vaapi",
                (VideoCodec.Av1Aom, _, true) => "av1_vaapi",
                (VideoCodec.Av1Svt, _, true) => "av1_vaapi",
                (VideoCodec.Vp8, _, true) => "vp8_vaapi",
                (VideoCodec.Vp9, _, true) => "vp9_vaapi",
                _ => null
            };
            if (hw != null) return hw;
        }
        return o.VideoCodec switch
        {
            VideoCodec.H264 => "libx264",
            VideoCodec.H265 => "libx265",
            VideoCodec.Av1Aom => "libaom-av1",
            VideoCodec.Av1Svt => "libsvtav1",
            VideoCodec.Vp8 => "libvpx",
            VideoCodec.Vp9 => "libvpx-vp9",
            VideoCodec.Mpeg4 => "mpeg4",
            VideoCodec.Gif => "gif",
            _ => "libx264"
        };
    }

    // ══════════════════════════════ 源像素格式 → 目标（10bit/422/444 透传，逻辑与旧版一致）══════════════════════════════

    private static (int W, int H, int Depth) ParsePixFmt(string pixFmt)
    {
        string s = pixFmt.Trim().ToLowerInvariant();
        int paren = s.IndexOf('(');
        if (paren >= 0) s = s.Substring(0, paren).Trim();
        return s switch
        {
            "yuv420p" => (1, 1, 8),
            "yuvj420p" => (1, 1, 8),
            "yuv420p10le" => (1, 1, 10),
            "yuv420p10be" => (1, 1, 10),
            "yuv420p12le" => (1, 1, 12),
            "yuv420p12be" => (1, 1, 12),
            "yuv420p16le" => (1, 1, 12),
            "yuv422p" => (1, 0, 8),
            "yuvj422p" => (1, 0, 8),
            "yuv422p10le" => (1, 0, 10),
            "yuv422p10be" => (1, 0, 10),
            "yuv422p12le" => (1, 0, 12),
            "yuv444p" => (0, 0, 8),
            "yuvj444p" => (0, 0, 8),
            "yuv444p10le" => (0, 0, 10),
            "yuv444p10be" => (0, 0, 10),
            "yuv444p12le" => (0, 0, 12),
            "nv12" => (1, 1, 8),
            "nv21" => (1, 1, 8),
            "p010le" => (1, 1, 10),
            "p010be" => (1, 1, 10),
            "p012le" => (1, 1, 12),
            "yuyv422" => (1, 0, 8),
            "y210le" => (1, 0, 10),
            "y212le" => (1, 0, 12),
            "vuya" => (0, 0, 8),
            "gbrp" => (0, 0, 8),
            "gbrp10le" => (0, 0, 10),
            "gbrp12le" => (0, 0, 12),
            "rgb24" => (0, 0, 8),
            "bgr24" => (0, 0, 8),
            "rgba" => (0, 0, 8),
            "bgra" => (0, 0, 8),
            _ => (1, 1, 8)
        };
    }

    private static string? PlanarPixFmtName(int w, int h, int d) => (w, h, d) switch
    {
        (1, 1, 8) => "yuv420p",
        (1, 1, 10) => "yuv420p10le",
        (1, 1, 12) => "yuv420p12le",
        (1, 0, 8) => "yuv422p",
        (1, 0, 10) => "yuv422p10le",
        (1, 0, 12) => "yuv422p12le",
        (0, 0, 8) => "yuv444p",
        (0, 0, 10) => "yuv444p10le",
        (0, 0, 12) => "yuv444p12le",
        _ => null
    };

    private static string? PickTargetPixFmt(VideoTranscodeOptions o, string? sourcePixFmt)
    {
        if (o.Container == VideoContainer.Gif) return null;
        if (IsVaapiBackend(o.HardwareBackend)) return null; // vaapi 经 format=nv12,hwupload
        if (string.IsNullOrWhiteSpace(sourcePixFmt)) return "yuv420p";

        var (srcW, srcH, srcDepth) = ParsePixFmt(sourcePixFmt);
        if (srcDepth < 8) srcDepth = 8;

        // QSV 系统内存路径（与旧版阶梯一致）
        if (o.HardwareBackend == HardwareBackend.Intel && OperatingSystem.IsWindows())
        {
            if (o.VideoCodec == VideoCodec.H265)
            {
                if (srcW == 1 && srcH == 0)
                {
                    if (srcDepth >= 10) return "y210le";
                    return "yuyv422";
                }
                if (srcDepth >= 10) return "p010le";
                return "nv12";
            }
            return "nv12";
        }

        bool allowHighDepth = o.VideoCodec != VideoCodec.H264;
        var depths = new List<int>();
        foreach (int d in new[] { srcDepth, 10, 8 })
            if (d <= srcDepth && !depths.Contains(d)) depths.Add(d);
        var chromas = new List<(int W, int H)> { (srcW, srcH) };
        if (srcW != 1 || srcH != 1) chromas.Add((1, 1));

        foreach ((int w, int h) in chromas)
        {
            foreach (int d in depths)
            {
                if (!allowHighDepth && d > 8) continue;
                string? name = PlanarPixFmtName(w, h, d);
                if (name != null) return name;
            }
        }
        return "yuv420p";
    }

    // ══════════════════════════════ 杂项 ══════════════════════════════

    private static string BuildTmpPath(string outputPath)
    {
        string dir = Path.GetDirectoryName(outputPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(outputPath);
        string ext = Path.GetExtension(outputPath);
        return Path.Combine(dir, $"{name}.tmp{ext}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class ScaledProgress : IProgress<double>
    {
        private readonly IProgress<double>? _inner;
        private readonly double _offset;
        private readonly double _scale;
        public ScaledProgress(IProgress<double>? inner, double offset, double scale) { _inner = inner; _offset = offset; _scale = scale; }
        public void Report(double value) => _inner?.Report(_offset + value * _scale);
    }
}
