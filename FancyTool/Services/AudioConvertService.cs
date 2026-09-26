using FFmpeg.AutoGen;
using FancyToolAva.Models;
using Microsoft.Extensions.Logging;
using ATL;

namespace FancyToolAva.Services;

/// <summary>
/// 进程内音频转换器（移植自 music_player 的 FFmpegAudioConverter，并扩展更多输出格式）：
/// 解码 → swresample 重采样/位深（DSD 增益经浮点中间级）→ 编码封装，标签/封面经 ATL 读取后内联写入。
/// 位深决策（Auto 档，与 music_player 一致）：
///   wav/aiff : 24bit 源 → 24bit PCM；16bit/未知(非 DSD) → 16bit；其余(32bit/float/DSD) → float/32bit；
///   flac/alac : ≥24bit 或 DSD → 24bit，否则 16bit；
///   mp3/aac/ogg/opus/wma/ac3/mp2 : 有损，按 BitrateKbps 编码（vorbis 映射质量档）。
/// </summary>
public sealed unsafe class AudioConvertService : IAudioConvertService
{
    private readonly ILogger<AudioConvertService> _logger;

    private const int FlacCompressionLevel = 8;
    private static readonly int[] OpusRates = [8000, 12000, 16000, 24000, 48000];
    private const int DsdDefaultRate = 176400;

    public AudioConvertService(ILogger<AudioConvertService> logger)
    {
        _logger = logger;
    }

    /// <summary>随转换内联写入的源元数据（wav/aiff 不写：RIFF/IFF INFO 无 UTF-8 互操作）。</summary>
    public sealed record ConversionMetadata(string? Title, string? Artist, string? Album,
        int Track, int Disc, int Year, byte[]? CoverBytes, string? CoverMime);

    // ─────────────────────────── 探测 ───────────────────────────

    // Root the native callback; its opaque deadline lives until avformat_close_input returns.
    private static readonly AVIOInterruptCB_callback ProbeInterrupt = static opaque =>
        System.Diagnostics.Stopwatch.GetTimestamp() >= *(long*)opaque ? 1 : 0;

    public Task<AudioProbeInfo?> ProbeAsync(string inputPath, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException(inputPath);
        return Task.Run<AudioProbeInfo?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!FFmpegNative.TryLoad(out Exception? loadError))
                throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegLoadFailed", loadError!.Message));

            AVFormatContext* fmt = null;
            AVDictionary* options = null;
            long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + 10 * System.Diagnostics.Stopwatch.Frequency;
            try
            {
                fmt = ffmpeg.avformat_alloc_context();
                if (fmt == null) return null;
                fmt->interrupt_callback = new AVIOInterruptCB { callback = ProbeInterrupt, opaque = &deadline };
                fmt->probesize = 1024 * 1024;
                fmt->max_analyze_duration = 2 * ffmpeg.AV_TIME_BASE;
                ffmpeg.av_dict_set(&options, "protocol_whitelist", "file", 0);
                if (ffmpeg.avformat_open_input(&fmt, inputPath, null, &options) < 0 || fmt == null) return null;
                if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) return null;
                int si = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
                if (si < 0) return null;

                AVStream* stream = fmt->streams[si];
                AVCodecParameters* par = stream->codecpar;
                string codec = ffmpeg.avcodec_get_name(par->codec_id);
                int sampleRate = par->sample_rate;
                int channels = par->ch_layout.nb_channels;
                int bitDepth = par->bits_per_raw_sample > 0 ? par->bits_per_raw_sample
                    : ffmpeg.av_get_bits_per_sample(par->codec_id);
                bool isDsd = par->codec_id is AVCodecID.AV_CODEC_ID_DSD_LSBF or AVCodecID.AV_CODEC_ID_DSD_MSBF
                    or AVCodecID.AV_CODEC_ID_DSD_LSBF_PLANAR or AVCodecID.AV_CODEC_ID_DSD_MSBF_PLANAR;
                long containerBitRate = fmt->bit_rate > 0 ? fmt->bit_rate : par->bit_rate;
                int bitRateKbps = containerBitRate > 0 ? (int)Math.Min(int.MaxValue, Math.Round(containerBitRate / 1000.0)) : 0;

                double durationMs = 0;
                if (fmt->duration > 0)
                    durationMs = fmt->duration * 1000.0 / ffmpeg.AV_TIME_BASE;
                else if (stream->duration > 0)
                    durationMs = stream->duration * ffmpeg.av_q2d(stream->time_base) * 1000.0;
                if (!double.IsFinite(durationMs) || durationMs < 0 || durationMs >= TimeSpan.MaxValue.TotalMilliseconds)
                    durationMs = 0;

                return new AudioProbeInfo(inputPath, codec, sampleRate, channels, bitDepth, bitRateKbps, (long)durationMs, isDsd);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (options != null) ffmpeg.av_dict_free(&options);
                if (fmt != null) ffmpeg.avformat_close_input(&fmt);
            }
        }, ct);
    }

    // ─────────────────────────── 转换 ───────────────────────────

    public Task ConvertAsync(string inputPath, string outputPath, AudioConvertOptions options,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException(inputPath);
        return Task.Run(() =>
        {
            if (!FFmpegNative.TryLoad(out Exception? loadError))
                throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegLoadFailed", loadError!.Message));

            string tmpPath = outputPath + ".tmp";
            try
            {
                ConversionMetadata? metadata = options.CopyMetadata ? TryReadMetadata(inputPath) : null;
                Convert(inputPath, tmpPath, options, metadata, progress, ct);
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tmpPath, outputPath);
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
        }, ct);
    }

    private void Convert(string inputPath, string outputPath, AudioConvertOptions o,
        ConversionMetadata? metadata, IProgress<double>? progress, CancellationToken ct)
    {
        bool isDsd = Path.GetExtension(inputPath.AsSpan()).Equals(".dsf", StringComparison.OrdinalIgnoreCase)
                  || Path.GetExtension(inputPath.AsSpan()).Equals(".dff", StringComparison.OrdinalIgnoreCase);
        bool applyGain = Math.Abs(o.GainDb) > 0.01;
        double gainLinear = applyGain ? Math.Pow(10, o.GainDb / 20.0) : 1.0;

        AVFormatContext* inFmt = null;
        AVCodecContext* decCtx = null;
        AVFormatContext* ofmtCtx = null;
        AVCodecContext* encCtx = null;
        SwrContext* swr = null;
        SwrContext* gainSwr = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        AVFrame* decFrame = ffmpeg.av_frame_alloc();
        AVFrame* swrFrame = ffmpeg.av_frame_alloc();
        AVFrame* gainFrame = ffmpeg.av_frame_alloc();
        FrameFeeder? feeder = null;

        try
        {
            int ret = ffmpeg.avformat_open_input(&inFmt, inputPath, null, null);
            if (ret < 0) throw FFmpegNative.CreateException(ret, $"无法打开音频文件: {inputPath}");
            if (ffmpeg.avformat_find_stream_info(inFmt, null) < 0)
                throw new InvalidOperationException($"无法读取流信息: {inputPath}");

            int streamIndex = ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (streamIndex < 0)
            {
                // 实测用户库里的 .flac 有 RAR/ZIP 改名、0 字节空文件：把检测到的容器报出来，别只说"没有音频流"
                string container = VideoTranscodeService.PtrToString(inFmt->iformat->name);
                string size = new FileInfo(inputPath).Length.ToString("N0");
                throw new InvalidOperationException(
                    $"{LocalizationRegistry.Get("AudioTranscode.Msg_NoAudioStream")} 容器={container}，文件 {size} 字节（疑似非音频文件，被改了扩展名）: {inputPath}");
            }
            AVStream* inStream = inFmt->streams[streamIndex];

            AVCodec* decoder = ffmpeg.avcodec_find_decoder(inStream->codecpar->codec_id);
            if (decoder == null) throw new InvalidOperationException($"没有可用解码器: {inStream->codecpar->codec_id}");
            decCtx = ffmpeg.avcodec_alloc_context3(decoder);
            ffmpeg.avcodec_parameters_to_context(decCtx, inStream->codecpar);
            decCtx->thread_count = 0;
            if (ffmpeg.avcodec_open2(decCtx, decoder, null) < 0)
                throw new InvalidOperationException("解码器打开失败");

            // WAV/PCM 解码器常给出 UNSPEC 顺序布局；仅补全未知布局（E-AC-3 的 5.1(side) 不能替换）
            if (decCtx->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
            {
                int channels = Math.Max(1, decCtx->ch_layout.nb_channels);
                ffmpeg.av_channel_layout_uninit(&decCtx->ch_layout);
                ffmpeg.av_channel_layout_default(&decCtx->ch_layout, channels);
            }

            int depth = DetectBitDepth(decCtx, isDsd);
            int inRate = decCtx->sample_rate != 0 ? decCtx->sample_rate : 48000;
            int outRate = o.SampleRateHz > 0 ? o.SampleRateHz : inRate;
            if (isDsd && o.SampleRateHz == 0) outRate = DsdDefaultRate;

            SelectEncoder(o, depth, isDsd, ref outRate,
                out string muxerName, out AVCodecID encCodecId, out AVSampleFormat encFmt, out int flacBps);

            ffmpeg.avformat_alloc_output_context2(&ofmtCtx, null, muxerName, outputPath);
            if (ofmtCtx == null) throw new InvalidOperationException($"不支持的输出格式: {o.Format}");

            AVCodec* encoder = ffmpeg.avcodec_find_encoder(encCodecId);
            if (encoder == null) throw new InvalidOperationException($"没有可用编码器: {encCodecId}");

            encCtx = ffmpeg.avcodec_alloc_context3(encoder);
            encCtx->sample_fmt = encFmt;
            encCtx->sample_rate = outRate;
            // 对齐编码器实际支持的采样格式（构建差异兜底），并回写本地 encFmt 供 swr 管线使用
            encFmt = AlignSampleFormat(encCtx, encoder, encFmt);
            encCtx->sample_fmt = encFmt;
            bool lossless = IsLossless(encCodecId);
            encCtx->bit_rate = lossless ? 0 : Math.Max(16, o.BitrateKbps) * 1000L;
            if (encCodecId == AVCodecID.AV_CODEC_ID_VORBIS)
            {
                // libvorbis 受管 ABR 在高采样率下低于最低码率会直接失败；改用质量模式（等价 CLI -q:a）
                encCtx->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
                encCtx->global_quality = VorbisQualityFor(o.BitrateKbps) * ffmpeg.FF_QP2LAMBDA;
                encCtx->bit_rate = 0;
            }
            ffmpeg.av_channel_layout_copy(&encCtx->ch_layout, &decCtx->ch_layout);
            // lame 等编码器只接受原生声道布局；统一规范化。
            // av_channel_layout_uninit 会 memset 清零整个结构，声道数必须先取出再重设。
            int srcChannels = Math.Max(1, encCtx->ch_layout.nb_channels);
            ffmpeg.av_channel_layout_uninit(&encCtx->ch_layout);
            ffmpeg.av_channel_layout_default(&encCtx->ch_layout, srcChannels);
            SelectEncoderChannelLayout(encCtx, encoder);
            // 采样率对齐编码器支持表（lame 最高 48k、libopus 五档），避免高采样率直接失败
            outRate = SnapRateToEncoder(encCtx, encoder, outRate);
            encCtx->sample_rate = outRate;
            if (flacBps > 0) encCtx->bits_per_raw_sample = flacBps;
            if (encCodecId == AVCodecID.AV_CODEC_ID_FLAC) encCtx->compression_level = FlacCompressionLevel;
            if ((ofmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                encCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            int openChannels = encCtx->ch_layout.nb_channels; // open2 失败路径会重置 ch_layout，先取值
            ret = ffmpeg.avcodec_open2(encCtx, encoder, null);
            if (ret < 0)
                throw FFmpegNative.CreateException(ret, $"编码器打开失败 ({encCodecId}, {outRate} Hz, {openChannels} 声道, {encCtx->sample_fmt})");

            AVStream* outStream = ffmpeg.avformat_new_stream(ofmtCtx, null);
            outStream->time_base = new AVRational { num = 1, den = outRate };
            ffmpeg.avcodec_parameters_from_context(outStream->codecpar, encCtx);

            // 封面：mp3/flac/m4a 走附加图片流；ogg/opus 走 METADATA_BLOCK_PICTURE 注释。
            // 损坏封面（解析不出宽高，如截断 JPEG）必须跳过：mp4/m4a 封装器会因
            // 0x0 尺寸的视频流在 write_header 直接 EINVAL，导致整首歌失败。
            AVStream* picStream = null;
            if (metadata?.CoverBytes is { Length: > 0 } coverBytes && SupportsAttachedPicCover(muxerName, encCodecId))
            {
                var (cw, chh) = DetectImageDimensions(coverBytes);
                if (cw > 0 && chh > 0)
                    picStream = CreateAttachedPicStream(ofmtCtx, coverBytes);
                else
                    _logger.LogWarning("内嵌封面无法解析尺寸（疑似损坏），已跳过封面写入: {Input}", inputPath);
            }

            if ((ofmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ret = ffmpeg.avio_open(&ofmtCtx->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE);
                if (ret < 0) throw FFmpegNative.CreateException(ret, $"无法创建输出文件: {outputPath}");
            }

            // 标签在 write_header 时落入容器（wav/aiff 不写）
            if (metadata is not null && SupportsInlineTags(muxerName))
                WriteInlineTags(ofmtCtx, metadata, encCodecId);

            AVDictionary* headerOpts = null;
            if (muxerName == "ipod")
                ffmpeg.av_dict_set(&headerOpts, "movflags", "+faststart", 0); // m4a 元数据前置，利于流式播放
            int headerRet = ffmpeg.avformat_write_header(ofmtCtx, &headerOpts);
            ffmpeg.av_dict_free(&headerOpts);
            if (headerRet < 0)
                throw FFmpegNative.CreateException(headerRet, "写入文件头失败");

            // 附加封面包：首个写入，位于所有音频包之前
            if (picStream != null)
                WriteAttachedPic(ofmtCtx, picStream, metadata!.CoverBytes!);

            // 第一级重采样：解码格式 → 编码格式（DSD 需增益且目标为整型时先到 float，由第二级 gainSwr 施加增益）
            bool gainNeedsFloatStage = applyGain && !IsFloatFormat(encFmt);
            AVSampleFormat primaryFmt = gainNeedsFloatStage ? AVSampleFormat.AV_SAMPLE_FMT_FLT : encFmt;

            ffmpeg.swr_alloc_set_opts2(&swr, &encCtx->ch_layout, primaryFmt, outRate,
                &decCtx->ch_layout, decCtx->sample_fmt, decCtx->sample_rate, 0, null);
            if (swr == null) throw new InvalidOperationException("swresample 创建失败");
            if (ffmpeg.swr_init(swr) < 0) throw new InvalidOperationException("swresample 初始化失败");

            // 跟踪 swr 实际按什么输入参数初始化：拼接/损坏的 MP3 会在文件中途切换
            // 采样率/声道数（实测 44.1k 立体声 → 24k、结尾混入单声道帧），固定参数的
            // swresample 会返回 AVERROR_INPUT_CHANGED 让整首歌失败。参数变化时重建即可
            // 连续转换（与 ffmpeg CLI 的 aresample 行为一致）。
            AVSampleFormat swrInFmt = decCtx->sample_fmt;
            int swrInRate = decCtx->sample_rate;
            int swrInNbCh = decCtx->ch_layout.nb_channels;
            int swrInOrder = (int)decCtx->ch_layout.order;
            ulong swrInMask = decCtx->ch_layout.u.mask;

            bool SwrMatchesFrame(AVFrame* f) =>
                (AVSampleFormat)f->format == swrInFmt && f->sample_rate == swrInRate
                && f->ch_layout.nb_channels == swrInNbCh && (int)f->ch_layout.order == swrInOrder
                && (f->ch_layout.order != AVChannelOrder.AV_CHANNEL_ORDER_NATIVE
                    || f->ch_layout.u.mask == swrInMask);

            void RebuildSwr(ref SwrContext* swr, AVCodecContext* encoderCtx, AVFrame* f)
            {
                _logger.LogInformation("输入参数中途变化 {OldFmt}/{OldRate}Hz/{OldNb}ch → {NewFmt}/{NewRate}Hz/{NewNb}ch，重建重采样器: {Input}",
                    swrInFmt, swrInRate, swrInNbCh, (AVSampleFormat)f->format, f->sample_rate, f->ch_layout.nb_channels, inputPath);
                SwrContext* fresh = null;
                ffmpeg.swr_alloc_set_opts2(&fresh, &encoderCtx->ch_layout, primaryFmt, outRate,
                    &f->ch_layout, (AVSampleFormat)f->format, f->sample_rate, 0, null);
                if (fresh == null || ffmpeg.swr_init(fresh) < 0)
                    throw new InvalidOperationException("重采样器按新输入参数重建失败");
                SwrContext* old = swr;
                swr = fresh;
                if (old != null) ffmpeg.swr_free(&old);
                swrInFmt = (AVSampleFormat)f->format;
                swrInRate = f->sample_rate;
                swrInNbCh = f->ch_layout.nb_channels;
                swrInOrder = (int)f->ch_layout.order;
                swrInMask = f->ch_layout.u.mask;
            }

            if (gainNeedsFloatStage)
            {
                ffmpeg.swr_alloc_set_opts2(&gainSwr, &encCtx->ch_layout, encFmt, outRate,
                    &encCtx->ch_layout, AVSampleFormat.AV_SAMPLE_FMT_FLT, outRate, 0, null);
                if (gainSwr == null || ffmpeg.swr_init(gainSwr) < 0)
                    throw new InvalidOperationException("增益级初始化失败");
            }

            feeder = new FrameFeeder(encCtx, ofmtCtx, outStream, pkt);
            double totalSeconds = inFmt->duration > 0 ? inFmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            long samplesWritten = 0;
            double lastReported = -1;

            while ((ret = ffmpeg.av_read_frame(inFmt, pkt)) >= 0)
            {
                ct.ThrowIfCancellationRequested();
                if (pkt->stream_index == streamIndex)
                {
                    ffmpeg.avcodec_send_packet(decCtx, pkt);
                    while (ffmpeg.avcodec_receive_frame(decCtx, decFrame) >= 0)
                    {
                        if (!SwrMatchesFrame(decFrame)) RebuildSwr(ref swr, encCtx, decFrame);
                        ConvertFrame(decFrame, swr, gainSwr, gainFrame, swrFrame,
                            applyGain, gainLinear, primaryFmt, encFmt, outRate, &encCtx->ch_layout,
                            feeder, ref samplesWritten);
                        ffmpeg.av_frame_unref(decFrame);
                        ReportProgress(progress, totalSeconds, samplesWritten, outRate, ref lastReported);
                    }
                }
                ffmpeg.av_packet_unref(pkt);
            }
            if (ret < 0 && ret != ffmpeg.AVERROR_EOF && ret != -11 /*EAGAIN*/)
                throw FFmpegNative.CreateException(ret, $"读取输入失败: {inputPath}");

            // 冲洗解码器
            ffmpeg.avcodec_send_packet(decCtx, null);
            while (ffmpeg.avcodec_receive_frame(decCtx, decFrame) >= 0)
            {
                if (!SwrMatchesFrame(decFrame)) RebuildSwr(ref swr, encCtx, decFrame);
                ConvertFrame(decFrame, swr, gainSwr, gainFrame, swrFrame,
                    applyGain, gainLinear, primaryFmt, encFmt, outRate, &encCtx->ch_layout,
                    feeder, ref samplesWritten);
                ffmpeg.av_frame_unref(decFrame);
            }

            // 冲洗两级 swresample 的延迟缓冲
            FlushSwr(swr, gainSwr, gainFrame, swrFrame,
                applyGain, gainLinear, primaryFmt, encFmt, outRate, &encCtx->ch_layout,
                feeder, ref samplesWritten);

            feeder.Flush();

            // 冲洗编码器并写出剩余包
            ffmpeg.avcodec_send_frame(encCtx, null);
            while (ffmpeg.avcodec_receive_packet(encCtx, pkt) >= 0)
            {
                WritePacket(pkt, ofmtCtx, outStream, outRate);
            }

            ffmpeg.av_write_trailer(ofmtCtx);
            progress?.Report(1.0);
            _logger.LogInformation("Audio convert done: {Input} -> {Output} ({Codec}, {Rate} Hz)",
                inputPath, outputPath, encCodecId, outRate);
        }
        finally
        {
            feeder?.Dispose();
            ffmpeg.av_frame_free(&gainFrame);
            ffmpeg.av_frame_free(&swrFrame);
            ffmpeg.av_frame_free(&decFrame);
            ffmpeg.av_packet_free(&pkt);
            if (swr != null) ffmpeg.swr_free(&swr);
            if (gainSwr != null) ffmpeg.swr_free(&gainSwr);
            if (encCtx != null) ffmpeg.avcodec_free_context(&encCtx);
            if (ofmtCtx != null)
            {
                if (ofmtCtx->pb != null && (ofmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                    ffmpeg.avio_closep(&ofmtCtx->pb);
                ffmpeg.avformat_free_context(ofmtCtx);
            }
            if (decCtx != null) ffmpeg.avcodec_free_context(&decCtx);
            if (inFmt != null) ffmpeg.avformat_close_input(&inFmt);
        }
    }

    // ─────────────────────────── 格式选择（music_player 7 种 + aiff/wma/ac3/mp2/32 位 wav）───────────────────────────

    private static bool SupportsInlineTags(string muxer) => muxer is not ("wav" or "aiff");
    private static bool SupportsAttachedPicCover(string muxer, AVCodecID codec)
        => muxer == "mp3" || codec == AVCodecID.AV_CODEC_ID_FLAC || muxer == "ipod";
    private static bool SupportsPictureCommentCover(string muxer) => muxer is "ogg" or "opus";

    private static void SelectEncoder(AudioConvertOptions o, int depth, bool isDsd, ref int outRate,
        out string muxerName, out AVCodecID encCodecId, out AVSampleFormat encFmt, out int flacBps)
    {
        // 用户显式位深（仅 wav/aiff/flac/alac 生效）
        AudioBitDepthMode d = o.BitDepth;
        int effDepth = d switch
        {
            AudioBitDepthMode.S16 => 16,
            AudioBitDepthMode.S24 => 24,
            AudioBitDepthMode.S32 => 32,
            AudioBitDepthMode.F32 => 33, // float 哨兵
            _ => 0,
        };

        flacBps = 0;
        switch (o.Format)
        {
            case AudioConvertFormat.Wav:
                muxerName = "wav";
                int wDepth = effDepth != 0 ? effDepth : (depth == 24 ? 24 : (depth == 0 || depth == 16) && !isDsd ? 16 : 33);
                if (wDepth == 24) { encCodecId = AVCodecID.AV_CODEC_ID_PCM_S24LE; encFmt = AVSampleFormat.AV_SAMPLE_FMT_S32; }
                else if (wDepth == 16) { encCodecId = AVCodecID.AV_CODEC_ID_PCM_S16LE; encFmt = AVSampleFormat.AV_SAMPLE_FMT_S16; }
                else if (wDepth == 32) { encCodecId = AVCodecID.AV_CODEC_ID_PCM_S32LE; encFmt = AVSampleFormat.AV_SAMPLE_FMT_S32; }
                else { encCodecId = AVCodecID.AV_CODEC_ID_PCM_F32LE; encFmt = AVSampleFormat.AV_SAMPLE_FMT_FLT; }
                return;
            case AudioConvertFormat.Aiff:
                muxerName = "aiff";
                int aDepth = effDepth != 0 ? effDepth : (depth == 24 ? 24 : (depth == 0 || depth == 16) && !isDsd ? 16 : 33);
                if (aDepth == 24) { encCodecId = AVCodecID.AV_CODEC_ID_PCM_S24BE; encFmt = AVSampleFormat.AV_SAMPLE_FMT_S32; }
                else if (aDepth == 16) { encCodecId = AVCodecID.AV_CODEC_ID_PCM_S16BE; encFmt = AVSampleFormat.AV_SAMPLE_FMT_S16; }
                else if (aDepth == 32) { encCodecId = AVCodecID.AV_CODEC_ID_PCM_S32BE; encFmt = AVSampleFormat.AV_SAMPLE_FMT_S32; }
                else { encCodecId = AVCodecID.AV_CODEC_ID_PCM_F32BE; encFmt = AVSampleFormat.AV_SAMPLE_FMT_FLT; }
                return;
            case AudioConvertFormat.Flac:
                muxerName = "flac";
                encCodecId = AVCodecID.AV_CODEC_ID_FLAC;
                bool fHi = effDepth == 24 || effDepth == 33 || effDepth == 32 || (effDepth == 0 && (depth >= 24 || isDsd));
                if (fHi) { encFmt = AVSampleFormat.AV_SAMPLE_FMT_S32; flacBps = 24; }
                else { encFmt = AVSampleFormat.AV_SAMPLE_FMT_S16; flacBps = 16; }
                return;
            case AudioConvertFormat.AlacM4a:
                muxerName = "ipod"; // .m4a
                encCodecId = AVCodecID.AV_CODEC_ID_ALAC;
                bool lHi = effDepth == 24 || effDepth == 33 || effDepth == 32 || (effDepth == 0 && (depth >= 24 || isDsd));
                if (lHi) { encFmt = AVSampleFormat.AV_SAMPLE_FMT_S32P; flacBps = 24; }
                else { encFmt = AVSampleFormat.AV_SAMPLE_FMT_S16P; flacBps = 16; }
                return;
            case AudioConvertFormat.Mp3:
                muxerName = "mp3";
                encCodecId = AVCodecID.AV_CODEC_ID_MP3;
                encFmt = AVSampleFormat.AV_SAMPLE_FMT_S32P; // libmp3lame 首选
                return;
            case AudioConvertFormat.OggVorbis:
                muxerName = "ogg";
                encCodecId = AVCodecID.AV_CODEC_ID_VORBIS;
                encFmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP; // libvorbis 仅 fltp
                return;
            case AudioConvertFormat.Opus:
                muxerName = "opus";
                encCodecId = AVCodecID.AV_CODEC_ID_OPUS;
                encFmt = AVSampleFormat.AV_SAMPLE_FMT_FLT;
                // Opus 只工作在 8/12/16/24/48 kHz；用户指定率优先
                if (outRate > 0)
                {
                    int best = OpusRates[0];
                    foreach (int r in OpusRates)
                        if (Math.Abs(r - outRate) < Math.Abs(best - outRate)) best = r;
                    outRate = best;
                }
                return;
            case AudioConvertFormat.AacM4a:
                muxerName = "ipod"; // .m4a
                encCodecId = AVCodecID.AV_CODEC_ID_AAC;
                encFmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                return;
            case AudioConvertFormat.Wma:
                muxerName = "asf"; // .wma
                encCodecId = AVCodecID.AV_CODEC_ID_WMAV2;
                encFmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP; // FFmpeg 9 的 wmav2 只接受 fltp
                return;
            case AudioConvertFormat.Ac3:
                muxerName = "ac3";
                encCodecId = AVCodecID.AV_CODEC_ID_AC3;
                encFmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                return;
            case AudioConvertFormat.Mp2:
                muxerName = "mp2";
                encCodecId = AVCodecID.AV_CODEC_ID_MP2;
                encFmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
                return;
            default:
                throw new InvalidOperationException($"不支持的输出格式: {o.Format}");
        }
    }

    public static string GetExtension(AudioConvertFormat f) => f switch
    {
        AudioConvertFormat.Mp3 => ".mp3",
        AudioConvertFormat.AacM4a => ".m4a",
        AudioConvertFormat.Flac => ".flac",
        AudioConvertFormat.AlacM4a => ".m4a",
        AudioConvertFormat.Wav => ".wav",
        AudioConvertFormat.Aiff => ".aiff",
        AudioConvertFormat.OggVorbis => ".ogg",
        AudioConvertFormat.Opus => ".opus",
        AudioConvertFormat.Wma => ".wma",
        AudioConvertFormat.Ac3 => ".ac3",
        AudioConvertFormat.Mp2 => ".mp2",
        _ => ".bin",
    };

    // ─────────────────────────── 元数据（ATL 读取 → av_dict/附加封面流写入）───────────────────────────

    private ConversionMetadata? TryReadMetadata(string path)
    {
        try
        {
            var track = new Track(path);
            byte[]? cover = null;
            string? mime = null;
            var pic = track.EmbeddedPictures?.FirstOrDefault(p => p.PictureData is { Length: > 0 });
            if (pic != null)
            {
                cover = pic.PictureData;
                mime = pic.MimeType;
            }
            return new ConversionMetadata(
                string.IsNullOrWhiteSpace(track.Title) ? null : track.Title,
                string.IsNullOrWhiteSpace(track.Artist) ? null : track.Artist,
                string.IsNullOrWhiteSpace(track.Album) ? null : track.Album,
                track.TrackNumber ?? 0, track.DiscNumber ?? 0, track.Year ?? 0, cover, mime);
        }
        catch
        {
            return null; // 标签读不出不影响转换
        }
    }

    private static void WriteInlineTags(AVFormatContext* ofmtCtx, ConversionMetadata meta, AVCodecID codec)
    {
        void Set(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            ffmpeg.av_dict_set(&ofmtCtx->metadata, key, value, 0);
        }

        Set("title", meta.Title);
        Set("artist", meta.Artist);
        Set("album", meta.Album);
        if (meta.Track > 0) Set("track", meta.Track.ToString());
        if (meta.Disc > 0) Set("disc", meta.Disc.ToString());
        if (meta.Year > 0) Set("date", meta.Year.ToString());

        // vorbis 系（flac/ogg/opus）用大写 LYRICS；mp3 只能写成 TXXX，不内联歌词
        string muxer = VideoTranscodeService.PtrToString(ofmtCtx->oformat->name);
        if (meta.CoverBytes is { Length: > 0 } cover && SupportsPictureCommentCover(muxer))
        {
            var (w, h) = DetectImageDimensions(cover);
            if (w <= 0 || h <= 0) return; // 损坏封面不写入注释（无意义且膨胀文件）
            string m = string.IsNullOrWhiteSpace(meta.CoverMime) ? DetectImageMime(cover) : meta.CoverMime;
            Set("METADATA_BLOCK_PICTURE", System.Convert.ToBase64String(BuildFlacPictureBlock(m, cover)));
        }
        _ = codec;
    }

    /// <summary>创建附加封面流（mp3/flac/m4a）：codec 按图片签名判定，不参与编码。</summary>
    private static AVStream* CreateAttachedPicStream(AVFormatContext* ofmtCtx, byte[] cover)
    {
        AVStream* picStream = ffmpeg.avformat_new_stream(ofmtCtx, null);
        picStream->disposition = ffmpeg.AV_DISPOSITION_ATTACHED_PIC;
        picStream->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
        picStream->codecpar->codec_id = DetectImageCodec(cover);
        picStream->codecpar->format = -1;
        var (w, h) = DetectImageDimensions(cover);
        picStream->codecpar->width = w;
        picStream->codecpar->height = h;
        return picStream;
    }

    /// <summary>把封面字节作为单包写入附加图片流（紧随 write_header、先于所有音频包）。</summary>
    private static void WriteAttachedPic(AVFormatContext* ofmtCtx, AVStream* picStream, byte[] cover)
    {
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        try
        {
            byte* buf = (byte*)ffmpeg.av_malloc((ulong)cover.Length);
            System.Runtime.InteropServices.Marshal.Copy(cover, 0, (IntPtr)buf, cover.Length);
            ffmpeg.av_packet_from_data(pkt, buf, cover.Length);
            pkt->stream_index = picStream->index;
            pkt->pts = 0;
            pkt->dts = 0;
            ffmpeg.av_packet_rescale_ts(pkt, new AVRational { num = 1, den = 1000 }, picStream->time_base);
            ffmpeg.av_write_frame(ofmtCtx, pkt);
            ffmpeg.av_packet_unref(pkt);
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
        }
    }

    private static AVCodecID DetectImageCodec(byte[] data)
    {
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8) return AVCodecID.AV_CODEC_ID_MJPEG;
        if (data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return AVCodecID.AV_CODEC_ID_PNG;
        return AVCodecID.AV_CODEC_ID_MJPEG;
    }

    private static string DetectImageMime(byte[] data)
        => DetectImageCodec(data) == AVCodecID.AV_CODEC_ID_PNG ? "image/png" : "image/jpeg";

    /// <summary>解析图片尺寸：JPEG 扫 SOF0/SOF2 标记，PNG 读 IHDR 定长偏移；失败返回 (0,0)。</summary>
    private static (int Width, int Height) DetectImageDimensions(byte[] data)
    {
        if (data.Length > 24 && data[0] == 0x89 && data[1] == 0x50) // PNG：IHDR 固定在 16..23
        {
            int w = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
            int h = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
            return (w, h);
        }
        if (data.Length > 9 && data[0] == 0xFF && data[1] == 0xD8) // JPEG：扫 SOF0/SOF2
        {
            for (int i = 2; i + 9 < data.Length;)
            {
                if (data[i] != 0xFF) { i++; continue; }
                byte marker = data[i + 1];
                if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3)
                {
                    int h = (data[i + 5] << 8) | data[i + 6];
                    int w = (data[i + 7] << 8) | data[i + 8];
                    return (w, h);
                }
                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; } // 无长度段
                int segLen = (data[i + 2] << 8) | data[i + 3];
                if (segLen < 2) break;
                i += 2 + segLen;
            }
        }
        return (0, 0);
    }

    /// <summary>构造 FLAC PICTURE 元数据块（ogg/opus 的 METADATA_BLOCK_PICTURE 载荷），全大端。</summary>
    private static byte[] BuildFlacPictureBlock(string mime, byte[] image)
    {
        byte[] mimeBytes = System.Text.Encoding.ASCII.GetBytes(mime);
        using var ms = new MemoryStream(32 + mimeBytes.Length + image.Length);
        void WriteU32(uint v)
        {
            ms.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        }
        WriteU32(0x00000003);              // picture type: front cover
        WriteU32((uint)mimeBytes.Length); ms.Write(mimeBytes);
        WriteU32(0);                       // description: 空
        WriteU32(0); WriteU32(0); WriteU32(0); // width/height/depth: 未知
        WriteU32(0);                       // colors: 未知
        WriteU32((uint)image.Length); ms.Write(image);
        return ms.ToArray();
    }

    // ─────────────────────────── 内部管线 ───────────────────────────

    private static void ConvertFrame(AVFrame* decFrame, SwrContext* swr, SwrContext* gainSwr, AVFrame* gainFrame,
        AVFrame* swrFrame, bool applyGain, double gainLinear, AVSampleFormat primaryFmt, AVSampleFormat encFmt,
        int outRate, AVChannelLayout* outLayout, FrameFeeder feeder, ref long samplesWritten)
    {
        PrepareFrame(swrFrame, primaryFmt, outRate, outLayout);
        int ret = ffmpeg.swr_convert_frame(swr, swrFrame, decFrame);
        if (ret < 0) throw FFmpegNative.CreateException(ret, "重采样失败");
        if (swrFrame->nb_samples == 0) return;

        samplesWritten += swrFrame->nb_samples;

        if (gainSwr != null)
        {
            ApplyGain(swrFrame, gainLinear); // 浮点域施加增益
            PrepareFrame(gainFrame, encFmt, outRate, outLayout);
            ret = ffmpeg.swr_convert_frame(gainSwr, gainFrame, swrFrame);
            if (ret < 0) throw FFmpegNative.CreateException(ret, "增益级转换失败");
            if (gainFrame->nb_samples > 0) feeder.Push(gainFrame);
        }
        else
        {
            if (applyGain) ApplyGain(swrFrame, gainLinear);
            feeder.Push(swrFrame);
        }
    }

    private static void FlushSwr(SwrContext* swr, SwrContext* gainSwr, AVFrame* gainFrame, AVFrame* swrFrame,
        bool applyGain, double gainLinear, AVSampleFormat primaryFmt, AVSampleFormat encFmt,
        int outRate, AVChannelLayout* outLayout, FrameFeeder feeder, ref long samplesWritten)
    {
        while (true)
        {
            PrepareFrame(swrFrame, primaryFmt, outRate, outLayout);
            int ret = ffmpeg.swr_convert_frame(swr, swrFrame, null);
            if (ret < 0 || swrFrame->nb_samples == 0) break;

            samplesWritten += swrFrame->nb_samples;
            if (gainSwr != null)
            {
                ApplyGain(swrFrame, gainLinear);
                while (true)
                {
                    PrepareFrame(gainFrame, encFmt, outRate, outLayout);
                    ret = ffmpeg.swr_convert_frame(gainSwr, gainFrame, swrFrame);
                    if (ret < 0 || gainFrame->nb_samples == 0) break;
                    feeder.Push(gainFrame);
                    if (ffmpeg.swr_get_out_samples(gainSwr, 0) <= 0) break;
                }
            }
            else
            {
                if (applyGain) ApplyGain(swrFrame, gainLinear);
                feeder.Push(swrFrame);
            }
            if (ffmpeg.swr_get_out_samples(swr, 0) <= 0) break;
        }
    }

    /// <summary>av_frame_unref 会清空全部字段，重设 swr_convert_frame 要求的输出帧参数。</summary>
    private static void PrepareFrame(AVFrame* frame, AVSampleFormat fmt, int outRate, AVChannelLayout* outLayout)
    {
        ffmpeg.av_frame_unref(frame);
        frame->format = (int)fmt;
        frame->sample_rate = outRate;
        ffmpeg.av_channel_layout_copy(&frame->ch_layout, outLayout);
    }

    private static void ApplyGain(AVFrame* frame, double linear)
    {
        if (frame->format == (int)AVSampleFormat.AV_SAMPLE_FMT_FLT)
        {
            float* p = (float*)frame->data[0];
            int n = frame->nb_samples * frame->ch_layout.nb_channels;
            for (int i = 0; i < n; i++) p[i] = (float)(p[i] * linear);
        }
        else if (frame->format == (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP)
        {
            for (int ch = 0; ch < frame->ch_layout.nb_channels; ch++)
            {
                float* p = (float*)frame->data[(uint)ch];
                for (int i = 0; i < frame->nb_samples; i++) p[i] = (float)(p[i] * linear);
            }
        }
    }

    private static void WritePacket(AVPacket* pkt, AVFormatContext* ofmtCtx, AVStream* stream, int encRate)
    {
        ffmpeg.av_packet_rescale_ts(pkt,
            new AVRational { num = 1, den = encRate },
            stream->time_base);
        pkt->stream_index = stream->index;
        ffmpeg.av_interleaved_write_frame(ofmtCtx, pkt);
        ffmpeg.av_packet_unref(pkt);
    }

    private static void ReportProgress(IProgress<double>? progress, double totalSeconds, long samplesWritten,
        int outRate, ref double lastReported)
    {
        if (progress == null) return;
        if (totalSeconds <= 0) return;
        double p = Math.Clamp(samplesWritten / (double)outRate / totalSeconds, 0, 1);
        if (p - lastReported >= 0.01 || p >= 1.0)
        {
            lastReported = p;
            progress.Report(p);
        }
    }

    private static bool IsFloatFormat(AVSampleFormat fmt)
        => fmt is AVSampleFormat.AV_SAMPLE_FMT_FLT or AVSampleFormat.AV_SAMPLE_FMT_FLTP;

    private static bool IsLossless(AVCodecID id)
        => id is AVCodecID.AV_CODEC_ID_FLAC or AVCodecID.AV_CODEC_ID_ALAC
            or AVCodecID.AV_CODEC_ID_PCM_S16LE or AVCodecID.AV_CODEC_ID_PCM_S24LE
            or AVCodecID.AV_CODEC_ID_PCM_S32LE or AVCodecID.AV_CODEC_ID_PCM_F32LE
            or AVCodecID.AV_CODEC_ID_PCM_S16BE or AVCodecID.AV_CODEC_ID_PCM_S24BE
            or AVCodecID.AV_CODEC_ID_PCM_S32BE or AVCodecID.AV_CODEC_ID_PCM_F32BE;

    /// <summary>码率档位映射到 libvorbis 质量档（44.1k 立体声 VBR 参考码率）。</summary>
    private static int VorbisQualityFor(int kbps) => kbps switch
    {
        >= 300 => 9,   // ~320k
        >= 220 => 8,   // ~256k
        >= 170 => 7,   // ~224k
        >= 140 => 5,   // ~160k
        >= 110 => 4,   // ~128k
        _ => 2,        // ~96k
    };

    /// <summary>把采样率对齐到编码器支持表中最接近的一档；编码器未声明则原样返回。</summary>
    private static int SnapRateToEncoder(AVCodecContext* encCtx, AVCodec* encoder, int rate)
    {
        void* cfg = null;
        int count = 0;
        int ret = ffmpeg.avcodec_get_supported_config(encCtx, encoder,
            AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_RATE, 0, &cfg, &count);
        if (ret < 0 || cfg == null || count <= 0) return rate;
        int* rates = (int*)cfg;
        int best = 0, bestDiff = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            int diff = Math.Abs(rates[i] - rate);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = rates[i];
            }
        }
        return best != 0 ? best : rate;
    }

    /// <summary>所选采样格式不在编码器支持表时改用其首选格式（如构建差异导致的 s16/fltp 变化）。</summary>
    private static AVSampleFormat AlignSampleFormat(AVCodecContext* encCtx, AVCodec* encoder, AVSampleFormat want)
    {
        void* cfg = null;
        int count = 0;
        int ret = ffmpeg.avcodec_get_supported_config(encCtx, encoder,
            AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_FORMAT, 0, &cfg, &count);
        if (ret < 0 || cfg == null || count <= 0) return want;
        AVSampleFormat* fmts = (AVSampleFormat*)cfg;
        for (int i = 0; i < count; i++)
            if (fmts[i] == want) return want;
        return fmts[0];
    }

    // 优先使用当前布局或同声道数布局；不支持多声道的编码器（如 MP3）回退到立体声/单声道，
    // 由 swresample 实际下混，不能只改输入帧的标签。
    private static void SelectEncoderChannelLayout(AVCodecContext* encCtx, AVCodec* encoder)
    {
        void* config = null;
        int count = 0;
        int ret = ffmpeg.avcodec_get_supported_config(encCtx, encoder,
            AVCodecConfig.AV_CODEC_CONFIG_CHANNEL_LAYOUT, 0, &config, &count);
        if (ret < 0 || config == null || count <= 0) return;
        AVChannelLayout* layouts = (AVChannelLayout*)config;
        AVChannelLayout* selected = null;
        int channels = encCtx->ch_layout.nb_channels;
        for (int i = 0; i < count; i++)
        {
            if (ffmpeg.av_channel_layout_compare(&encCtx->ch_layout, &layouts[i]) == 0) return;
            int candidateChannels = layouts[i].nb_channels;
            if (candidateChannels == channels ||
                (candidateChannels <= Math.Min(2, channels) &&
                 (selected == null || candidateChannels > selected->nb_channels)))
                selected = &layouts[i];
        }
        if (selected == null) throw new InvalidOperationException($"编码器不支持 {channels} 声道且没有兼容的下混布局");
        ffmpeg.av_channel_layout_uninit(&encCtx->ch_layout);
        ret = ffmpeg.av_channel_layout_copy(&encCtx->ch_layout, selected);
        if (ret < 0) throw FFmpegNative.CreateException(ret, "设置编码器声道布局失败");
    }

    /// <summary>源位深：优先解码器 bits_per_raw_sample（FLAC/DSD 容器会给出），其次按编码器推断；DSD 恒为 1。</summary>
    private static int DetectBitDepth(AVCodecContext* decCtx, bool isDsd)
    {
        if (isDsd) return 1;
        if (decCtx->bits_per_raw_sample > 0) return decCtx->bits_per_raw_sample;
        return decCtx->codec_id switch
        {
            AVCodecID.AV_CODEC_ID_PCM_S16LE or AVCodecID.AV_CODEC_ID_PCM_S16BE
                or AVCodecID.AV_CODEC_ID_PCM_U8 => 16,
            AVCodecID.AV_CODEC_ID_PCM_S24LE or AVCodecID.AV_CODEC_ID_PCM_S24BE => 24,
            AVCodecID.AV_CODEC_ID_PCM_S32LE or AVCodecID.AV_CODEC_ID_PCM_S32BE
                or AVCodecID.AV_CODEC_ID_PCM_F32LE or AVCodecID.AV_CODEC_ID_PCM_F32BE => 32,
            AVCodecID.AV_CODEC_ID_PCM_F64LE or AVCodecID.AV_CODEC_ID_PCM_F64BE => 64,
            _ => 0,
        };
    }

    /// <summary>固定 frame_size 编码器（如 libopus 960 采样）的分帧供料器，每帧送编后立即取包写出。</summary>
    private sealed unsafe class FrameFeeder : IDisposable
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

        public FrameFeeder(AVCodecContext* encCtx, AVFormatContext* ofmt, AVStream* stream, AVPacket* pkt)
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
                throw new InvalidOperationException("编码帧缓冲分配失败");
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
            if (ret < 0) throw FFmpegNative.CreateException(ret, "编码失败");
            while (ffmpeg.avcodec_receive_packet(_encCtx, _pkt) >= 0)
            {
                ffmpeg.av_packet_rescale_ts(_pkt,
                    new AVRational { num = 1, den = _encCtx->sample_rate },
                    _stream->time_base);
                _pkt->stream_index = _stream->index;
                ffmpeg.av_interleaved_write_frame(_ofmt, _pkt);
                ffmpeg.av_packet_unref(_pkt);
            }
        }

        private void ReallocFrame()
        {
            ffmpeg.av_frame_unref(_frame);
            _frame->format = (int)_encCtx->sample_fmt;
            _frame->sample_rate = _encCtx->sample_rate;
            ffmpeg.av_channel_layout_copy(&_frame->ch_layout, &_encCtx->ch_layout);
            _frame->nb_samples = _capacity > 0 ? _capacity : 1152;
            if (ffmpeg.av_frame_get_buffer(_frame, 0) < 0)
                throw new InvalidOperationException("编码帧缓冲分配失败");
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
}
