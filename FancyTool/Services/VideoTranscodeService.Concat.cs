using FancyToolAva.Models;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

/// <summary>
/// 拼接模式管线（与 <see cref="VideoTranscodeService.ConvertOnce"/> 平行的多段单编码实现）：
/// 顺序解码各片段（每段按循环次数重复解码），逐帧归一化到第一段（基准段）的画面参数后，
/// 喂入同一个滤镜图 → 编码器 → 封装器，输出单文件。分辨率/像素格式不一致的片段按显示宽高比
/// 等比缩放居中并补黑边；音频经段内独立 swresample 统一到基准段派生的编码器格式。
/// 不支持 AI 超分与插帧（其管线从单一输入派生）；拼接解码固定走软件路径（硬件编码不受影响）。
/// </summary>
public sealed unsafe partial class VideoTranscodeService
{
    private const int ConcatMaxLoopCount = 99;

    // ══════════════════════════════ 拼接入口 ══════════════════════════════

    /// <inheritdoc />
    public Task ConcatTranscodeAsync(IReadOnlyList<VideoConcatSegment> segments, string outputPath,
        VideoTranscodeOptions options, IProgress<double>? progress = null, CancellationToken ct = default,
        IProgress<VideoPreviewFrame>? preview = null, IProgress<VideoEncodingProgress>? statistics = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
            throw new ArgumentException(LocalizationRegistry.Get("VideoTranscode.Msg_ConcatEmpty"), nameof(segments));
        if (options.SuperResolutionEnabled || options.InterpolationMode != VideoInterpolationMode.None)
            throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_ConcatPipelineUnsupported"));
        foreach (var segment in segments)
            if (!File.Exists(segment.FilePath)) throw new FileNotFoundException(segment.FilePath);

        return Task.Run(() =>
        {
            if (!FFmpegNative.TryLoad(out Exception? loadError))
                throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegLoadFailed", loadError!.Message));
            ct.ThrowIfCancellationRequested();

            var probed = new (VideoConcatSegment Segment, SourceInfo Source)[segments.Count];
            var effectiveSec = new double[segments.Count];
            double totalSec = 0;
            double maxFps = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                var src = ProbeInternal(segment.FilePath);
                if (!src.Probe.HasVideo)
                    throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_ConcatNoVideo", segment.FilePath));
                long durationMs = src.Probe.DurationMs;
                ValidateTrim(options.WithTrim(segment.TrimStartSeconds, segment.TrimEndSeconds), durationMs);
                probed[i] = (segment, src);
                double duration = durationMs > 0 ? durationMs / 1000.0 : double.PositiveInfinity;
                double end = Math.Min(segment.TrimEndSeconds ?? duration, duration);
                effectiveSec[i] = double.IsFinite(end) ? Math.Max(0, end - segment.TrimStartSeconds) : 0;
                totalSec += effectiveSec[i] * Math.Clamp(segment.LoopCount, 1, ConcatMaxLoopCount);
                // 编码器 time_base 取全段最高帧率：低帧率基准会让高帧率段的重标定 pts 重复（muxer 非单调 DTS）
                maxFps = Math.Max(maxFps, src.Probe.Fps);
            }
            double referenceFps = maxFps > 0 ? maxFps : 30;

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
                        _logger.LogInformation("FFmpeg in-process concat 2-pass pass1: {Count} segments, encoder={Enc}", segments.Count, encoderName);
                        using (var pass1 = new ConcatSession(this, probed, effectiveSec, pass1Out, options, pass: 1, passLog, totalSec, referenceFps,
                            new ScaledProgress(progress, 0, 0.5), ct, preview, statistics)) pass1.Run();
                        _logger.LogInformation("FFmpeg in-process concat 2-pass pass2 -> {Output}", outputPath);
                        using (var pass2 = new ConcatSession(this, probed, effectiveSec, tmpPath, options, pass: 2, passLog, totalSec, referenceFps,
                            new ScaledProgress(progress, 0.5, 0.5), ct, preview, statistics)) pass2.Run();
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
                    _logger.LogInformation("FFmpeg in-process concat: {Count} segments -> {Output} encoder={Enc} muxer={Fmt}",
                        segments.Count, outputPath, encoderName, GetMuxerName(options.Container));
                    using (var session = new ConcatSession(this, probed, effectiveSec, tmpPath, options, pass: 0, null, totalSec, referenceFps,
                        progress, ct, preview, statistics)) session.Run();
                }

                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tmpPath, outputPath);
                progress?.Report(1.0);
                _logger.LogInformation("Concat done: {Count} segments -> {Output}", segments.Count, outputPath);
            }
            catch
            {
                TryDelete(tmpPath);
                throw;
            }
        }, ct);
    }

    /// <summary>一次完整的拼接编码会话：共享滤镜/编码/封装管线 + 逐段（×循环）解码。</summary>
    private sealed unsafe class ConcatSession : IDisposable
    {
        private readonly VideoTranscodeService _owner;
        private readonly IReadOnlyList<(VideoConcatSegment Segment, SourceInfo Source)> _segments;
        private readonly double[] _effectiveSec;
        private readonly string _outputPath;
        private readonly VideoTranscodeOptions _options;
        private readonly int _pass;
        private readonly string? _passLogFile;
        private readonly double _totalSec;
        private readonly double _referenceFps;
        private readonly IProgress<double>? _progress;
        private readonly CancellationToken _ct;
        private readonly IProgress<VideoPreviewFrame>? _preview;
        private readonly IProgress<VideoEncodingProgress>? _statisticsReporter;

        // 共享输出管线（所有段共用；分配/释放见 Cleanup）
        private AVFormatContext* _ofmt;
        private AVCodecContext* _vEnc;
        private AVCodecContext* _aEnc;
        private AVFilterGraph* _graph;
        private AVFilterContext* _srcCtx;
        private AVFilterContext* _sinkCtx;
        private AVBufferRef* _hwDevice;
        private AVBufferRef* _hwFrames;
        private EncoderFrameFeeder? _audioFeeder;
        private AVStream* _vOutStream;
        private AVStream* _aOutStream;
        private AVPacket* _encPkt;
        private AVPacket* _decPkt;
        private AVFrame* _decFrame;
        private AVFrame* _filtFrame;
        private AVFrame* _swrFrame;
        private readonly PreviewConverter _previewConverter = new();
        private EncodingProgress? _statistics;
        private IProgress<double>? _frameProgress;
        private bool _outputOpened;
        private bool _conversionFailed;

        // 基准段画面参数（片段归一化目标）
        private int _refWidth;
        private int _refHeight;
        private AVPixelFormat _refFormat;
        private AVRational _refSar;
        private AVColorRange _refColorRange;
        private double _refTbSec;

        public ConcatSession(VideoTranscodeService owner, IReadOnlyList<(VideoConcatSegment Segment, SourceInfo Source)> segments,
            double[] effectiveSec, string outputPath, VideoTranscodeOptions options, int pass, string? passLogFile,
            double totalSec, double referenceFps, IProgress<double>? progress, CancellationToken ct, IProgress<VideoPreviewFrame>? preview,
            IProgress<VideoEncodingProgress>? statistics)
        {
            _owner = owner;
            _segments = segments;
            _effectiveSec = effectiveSec;
            _outputPath = outputPath;
            _options = options;
            _pass = pass;
            _passLogFile = passLogFile;
            _totalSec = totalSec;
            _referenceFps = referenceFps;
            _progress = progress;
            _ct = ct;
            _preview = preview;
            _statisticsReporter = statistics;
            _encPkt = ffmpeg.av_packet_alloc();
            _decPkt = ffmpeg.av_packet_alloc();
            _decFrame = ffmpeg.av_frame_alloc();
            _filtFrame = ffmpeg.av_frame_alloc();
            _swrFrame = ffmpeg.av_frame_alloc();
        }

        public void Run()
        {
            _ct.ThrowIfCancellationRequested();
            try
            {
                SetupSharedPipeline();
                double globalOffsetSec = 0;
                for (int i = 0; i < _segments.Count; i++)
                {
                    int loops = Math.Clamp(_segments[i].Segment.LoopCount, 1, ConcatMaxLoopCount);
                    for (int loop = 0; loop < loops; loop++)
                    {
                        _ct.ThrowIfCancellationRequested();
                        DecodeSegmentOnce(_segments[i].Segment, _segments[i].Source, globalOffsetSec);
                        globalOffsetSec += _effectiveSec[i];
                    }
                }
                FlushPipeline();
            }
            catch
            {
                _conversionFailed = true;
                throw;
            }
            finally
            {
                Cleanup();
            }
        }

        /// <summary>打开基准段（第一段）派生共享管线参数并完成全部编码/封装初始化，随后释放基准段上下文。</summary>
        private void SetupSharedPipeline()
        {
            var (refSegment, refSource) = _segments[0];
            AVFormatContext* inFmt = null;
            AVCodecContext* vDec = null;
            AVCodecContext* aDec = null;
            try
            {
                AVDictionary* openOpts = null;
                ffmpeg.av_dict_set(&openOpts, "protocol_whitelist", "file", 0);
                int ret = ffmpeg.avformat_open_input(&inFmt, refSegment.FilePath, null, &openOpts);
                ffmpeg.av_dict_free(&openOpts);
                if (ret < 0) throw FFmpegNative.CreateException(ret, $"无法打开输入文件: {refSegment.FilePath}");
                if (ffmpeg.avformat_find_stream_info(inFmt, null) < 0)
                    throw new InvalidOperationException($"无法读取流信息: {refSegment.FilePath}");
                int vIdx = ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                if (vIdx < 0)
                    throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_ConcatNoVideo", refSegment.FilePath));
                AVStream* vInStream = inFmt->streams[vIdx];

                bool wantAudio = _options.Container != VideoContainer.Gif && _options.AudioCodec != AudioCodec.None;
                int aIdx = wantAudio
                    ? ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0)
                    : -1;

                vDec = OpenDecoder(vInStream);
                vDec->pkt_timebase = vInStream->time_base;
                if (aIdx >= 0) aDec = OpenAudioDecoderStream(inFmt->streams[aIdx]);

                // ── 基准画面参数（GIF 源解码为 PAL8，sws 无法以 PAL8 为目标格式，统一改用 BGRA 画布）──
                _refFormat = (AVPixelFormat)vDec->pix_fmt;
                if (_refFormat == AVPixelFormat.AV_PIX_FMT_NONE && vInStream->codecpar->format >= 0)
                    _refFormat = (AVPixelFormat)vInStream->codecpar->format;
                if (_refFormat == AVPixelFormat.AV_PIX_FMT_NONE) _refFormat = AVPixelFormat.AV_PIX_FMT_YUV420P;
                if (_refFormat == AVPixelFormat.AV_PIX_FMT_PAL8) _refFormat = AVPixelFormat.AV_PIX_FMT_BGRA;
                _refWidth = Math.Max(2, vDec->width);
                _refHeight = Math.Max(2, vDec->height);
                _refSar = vDec->sample_aspect_ratio.num > 0 && vDec->sample_aspect_ratio.den > 0
                    ? vDec->sample_aspect_ratio
                    : new AVRational { num = 1, den = 1 };
                _refColorRange = vDec->color_range == AVColorRange.AVCOL_RANGE_JPEG
                    ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG;
                // 拼接时间轴固定用 1/90000（对常见帧率整除性好）：基准段自身流 time_base 可能粗于其他段帧距，
                // 逐帧重标定会产生重复 pts（muxer 非单调 DTS）
                AVRational refTb = new() { num = 1, den = 90000 };
                _refTbSec = ffmpeg.av_q2d(refTb);
                var sourceColor = ReadColor(vDec);
                var outputColor = ResolveOutputColor(_options, sourceColor);

                // ── 输出封装 ──（字段不能直接取地址，经局部变量中转）
                AVFormatContext* ofmtLocal = null;
                ffmpeg.avformat_alloc_output_context2(&ofmtLocal, null, GetMuxerName(_options.Container), _outputPath);
                _ofmt = ofmtLocal;
                if (_ofmt == null) throw new InvalidOperationException($"不支持的输出容器: {_options.Container}");
                if (_options.Container == VideoContainer.Gif)
                    TrySetOpt(_ofmt->priv_data, "loop", _options.GifLoop <= 0 ? "0" : _options.GifLoop.ToString());

                // ── 视频编码器查询 + 共享滤镜图（缓冲源参数固定为基准段画布）──
                string encoderName = MapVideoEncoderName(_options);
                AVCodec* vCodecPtr = ffmpeg.avcodec_find_encoder_by_name(encoderName);
                if (vCodecPtr == null)
                    throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_EncoderUnavailable", encoderName));
                string? targetPixFmt = null;
                if (_options.Container != VideoContainer.Gif && !IsVaapiBackend(_options.HardwareBackend))
                    targetPixFmt = AlignTargetPixFmt(vCodecPtr, _options, refSource.PixFmt, outputColor.IsHdr);
                else if (IsVaapiBackend(_options.HardwareBackend) && outputColor.IsHdr) targetPixFmt = "p010le";

                _graph = ffmpeg.avfilter_graph_alloc();
                _srcCtx = CreateBufferSource(_graph, vDec, vInStream, _refWidth, _refHeight, _refFormat, refTb, _refSar);
                _sinkCtx = CreateBufferSink(_graph);
                string filterDesc = BuildFilterDescription(_options, targetPixFmt, BuildColorFilters(sourceColor, outputColor));
                AVBufferRef* hwDeviceLocal = null;
                if (!BindGraphAndConfigure(_graph, _srcCtx, _sinkCtx, filterDesc,
                        IsVaapiBackend(_options.HardwareBackend), "/dev/dri/renderD128", &hwDeviceLocal))
                    throw new InvalidOperationException($"滤镜图初始化失败: {filterDesc}");
                _hwDevice = hwDeviceLocal;

                // hwupload 之后 sink 协商出 hw 帧格式：为 vaapi 编码器派生同源 hw_frames_ctx
                if (_hwDevice != null)
                {
                    AVFilterLink* sinkLink = _sinkCtx->inputs[0];
                    _hwFrames = ffmpeg.av_hwframe_ctx_alloc(_hwDevice);
                    AVHWFramesContext* fc = (AVHWFramesContext*)_hwFrames->data;
                    fc->format = (AVPixelFormat)sinkLink->format;
                    fc->sw_format = outputColor.IsHdr ? AVPixelFormat.AV_PIX_FMT_P010LE : AVPixelFormat.AV_PIX_FMT_NV12;
                    fc->width = sinkLink->w;
                    fc->height = sinkLink->h;
                    if (ffmpeg.av_hwframe_ctx_init(_hwFrames) < 0)
                        throw new InvalidOperationException("VAAPI 硬件帧池初始化失败");
                }

                // ── 视频编码器 ──（标称帧率取全段最高帧率，保证最细帧距 ≥ 1 tick，输出按真实时间戳 VFR）
                _vEnc = ffmpeg.avcodec_alloc_context3(vCodecPtr);
                ConfigureVideoEncoder(_vEnc, _options, _pass, _passLogFile, _sinkCtx->inputs[0],
                    _hwDevice != null ? _hwFrames : null, _ofmt, _referenceFps, targetPixFmt, outputColor.IsHdr);
                SetEncoderColor(_vEnc, vInStream->codecpar, outputColor, outputColor.IsHdr && !sourceColor.IsHdr);
                int ret2 = ffmpeg.avcodec_open2(_vEnc, vCodecPtr, null);
                if (ret2 < 0) throw FFmpegNative.CreateException(ret2, $"视频编码器打开失败 ({encoderName})");

                _vOutStream = ffmpeg.avformat_new_stream(_ofmt, null);
                _vOutStream->time_base = _vEnc->time_base;
                ffmpeg.avcodec_parameters_from_context(_vOutStream->codecpar, _vEnc);

                // ── 音频编码器（基准段有音轨才建；其余段缺音轨时该区间静音）──
                if (aDec != null)
                {
                    AVStream* audioOut = null;
                    _aEnc = SetupAudioEncoder(_ofmt, aDec, _options, out audioOut, out SwrContext* refSwr);
                    _aOutStream = audioOut;
                    ffmpeg.swr_free(&refSwr); // 每段按各自解码器参数另建，见 DecodeSegmentOnce
                    _audioFeeder = new EncoderFrameFeeder(_aEnc, _ofmt, _aOutStream, _encPkt);
                }

                // ── 打开输出、写文件头 ──
                if ((_ofmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                {
                    int ret3 = ffmpeg.avio_open(&_ofmt->pb, _outputPath, ffmpeg.AVIO_FLAG_WRITE);
                    if (ret3 < 0) throw FFmpegNative.CreateException(ret3, $"无法创建输出文件: {_outputPath}");
                    _outputOpened = true;
                }
                ret2 = ffmpeg.avformat_write_header(_ofmt, null);
                if (ret2 < 0) throw FFmpegNative.CreateException(ret2, "写入文件头失败");

                _statistics = _statisticsReporter == null ? null : new EncodingProgress(_progress, _statisticsReporter,
                    _totalSec, ffmpeg.av_q2d(_vEnc->framerate), _pass == 0 ? 1 : _pass, _pass == 0 ? 1 : 2, null);
                _frameProgress = _statistics ?? _progress;
            }
            finally
            {
                if (aDec != null) ffmpeg.avcodec_free_context(&aDec);
                if (vDec != null) ffmpeg.avcodec_free_context(&vDec);
                if (inFmt != null) ffmpeg.avformat_close_input(&inFmt);
            }
        }

        /// <summary>解码一个片段（含其独立裁剪）并喂入共享管线；globalOffsetSec 为本段在成片时间轴上的起点。</summary>
        private void DecodeSegmentOnce(VideoConcatSegment segment, SourceInfo src, double globalOffsetSec)
        {
            AVFormatContext* inFmt = null;
            AVCodecContext* vDec = null;
            AVCodecContext* aDec = null;
            SwrContext* segSwr = null;
            ConcatVideoConverter? converter = null;
            try
            {
                AVDictionary* openOpts = null;
                ffmpeg.av_dict_set(&openOpts, "protocol_whitelist", "file", 0);
                int ret = ffmpeg.avformat_open_input(&inFmt, segment.FilePath, null, &openOpts);
                ffmpeg.av_dict_free(&openOpts);
                if (ret < 0) throw FFmpegNative.CreateException(ret, $"无法打开输入文件: {segment.FilePath}");
                if (ffmpeg.avformat_find_stream_info(inFmt, null) < 0)
                    throw new InvalidOperationException($"无法读取流信息: {segment.FilePath}");

                int vIdx = ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                if (vIdx < 0)
                    throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_ConcatNoVideo", segment.FilePath));
                AVStream* vInStream = inFmt->streams[vIdx];
                bool wantAudio = _options.Container != VideoContainer.Gif && _options.AudioCodec != AudioCodec.None;
                int aIdx = wantAudio
                    ? ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0)
                    : -1;

                vDec = OpenDecoder(vInStream);
                vDec->pkt_timebase = vInStream->time_base;
                if (aIdx >= 0) aDec = OpenAudioDecoderStream(inFmt->streams[aIdx]);

                var segOptions = _options.WithTrim(segment.TrimStartSeconds, segment.TrimEndSeconds);
                var timeline = new TrimTimeline(inFmt, vInStream, segOptions, src.Probe.DurationMs, aDec != null);

                // 无 pts 的解码帧按源帧率推算（帧时间戳统一用输入流 time_base）
                AVRational segTb = vInStream->time_base;
                double streamTbSec = ffmpeg.av_q2d(segTb);
                double srcFps = ffmpeg.av_q2d(vInStream->avg_frame_rate);
                if (srcFps <= 0) srcFps = 30;
                long frameStep = Math.Max(1, (long)Math.Round((1.0 / srcFps) / Math.Max(1e-9, streamTbSec)));
                double decodeStart = timeline.Origin;
                if (timeline.Start > 0)
                {
                    // 与单文件路径一致：按裁剪起点前 1 秒回退 seek，保留跨界音频包
                    double seekPosition = Math.Max(0, timeline.Start - 1);
                    long target = (long)Math.Round((timeline.Origin + seekPosition) / streamTbSec);
                    if (ffmpeg.av_seek_frame(inFmt, vIdx, target, ffmpeg.AVSEEK_FLAG_BACKWARD) >= 0)
                        decodeStart += seekPosition;
                }
                long nextPts = (long)Math.Round(decodeStart / streamTbSec);
                AVRational audioTimeBase = aIdx >= 0 ? inFmt->streams[aIdx]->time_base : default;
                if (aIdx >= 0) timeline.NextAudioPts = (long)Math.Round(decodeStart / ffmpeg.av_q2d(audioTimeBase));

                if (aDec != null)
                {
                    ffmpeg.swr_alloc_set_opts2(&segSwr,
                        &_aEnc->ch_layout, _aEnc->sample_fmt, _aEnc->sample_rate,
                        &aDec->ch_layout, aDec->sample_fmt, aDec->sample_rate, 0, null);
                    if (segSwr == null || ffmpeg.swr_init(segSwr) < 0)
                        throw new InvalidOperationException("音频重采样器初始化失败");
                }

                converter = new ConcatVideoConverter(_refWidth, _refHeight, _refFormat, _refSar, _refColorRange);
                long audioOffsetSamples = _aEnc != null ? (long)Math.Round(globalOffsetSec * _aEnc->sample_rate) : 0;
                bool audioOffsetApplied = false;

                while ((ret = ffmpeg.av_read_frame(inFmt, _decPkt)) >= 0)
                {
                    _ct.ThrowIfCancellationRequested();
                    if (_decPkt->stream_index == vIdx)
                    {
                        ret = SendVideoPacket(vDec, _decPkt);
                        if (ret == ErrEagain)
                        {
                            // 解码器输出满：先排空再重送一次（数据包不得丢弃）
                            ProcessSegmentVideoFrames(vDec, converter, timeline, segTb, globalOffsetSec, ref nextPts, frameStep);
                            ret = SendVideoPacket(vDec, _decPkt);
                        }
                        if (ret < 0 && ret != ErrEagain && ret != ffmpeg.AVERROR_EOF)
                            throw FFmpegNative.CreateException(ret, "视频解码失败");
                        ffmpeg.av_packet_unref(_decPkt);
                        ProcessSegmentVideoFrames(vDec, converter, timeline, segTb, globalOffsetSec, ref nextPts, frameStep);
                    }
                    else if (aDec != null && _decPkt->stream_index == aIdx)
                    {
                        ret = ffmpeg.avcodec_send_packet(aDec, _decPkt);
                        if (ret == ErrEagain)
                        {
                            ProcessSegmentAudioFrames(aDec, segSwr, timeline, audioTimeBase, audioOffsetSamples, ref audioOffsetApplied);
                            ret = ffmpeg.avcodec_send_packet(aDec, _decPkt);
                        }
                        if (ret < 0 && ret != ErrEagain && ret != ffmpeg.AVERROR_EOF)
                            throw FFmpegNative.CreateException(ret, "音频解码失败");
                        ffmpeg.av_packet_unref(_decPkt);
                        ProcessSegmentAudioFrames(aDec, segSwr, timeline, audioTimeBase, audioOffsetSamples, ref audioOffsetApplied);
                    }
                    else
                    {
                        ffmpeg.av_packet_unref(_decPkt);
                    }
                    if (timeline.VideoEnded && timeline.AudioEnded) break;
                }
                CheckReadError(ret, segment.FilePath);

                // 冲洗本段解码器与重采样器（滤镜图与编码器保持运行，段间不注入 EOF）
                SendVideoPacket(vDec, null);
                ProcessSegmentVideoFrames(vDec, converter, timeline, segTb, globalOffsetSec, ref nextPts, frameStep);
                if (aDec != null)
                {
                    ffmpeg.avcodec_send_packet(aDec, null);
                    ProcessSegmentAudioFrames(aDec, segSwr, timeline, audioTimeBase, audioOffsetSamples, ref audioOffsetApplied);
                    FlushSwr(segSwr, _swrFrame, _aEnc, _audioFeeder!, _options.AudioGainDb);
                }
            }
            finally
            {
                converter?.Dispose();
                if (segSwr != null) ffmpeg.swr_free(&segSwr);
                if (aDec != null) ffmpeg.avcodec_free_context(&aDec);
                if (vDec != null) ffmpeg.avcodec_free_context(&vDec);
                if (inFmt != null) ffmpeg.avformat_close_input(&inFmt);
            }
        }

        /// <summary>排空视频解码器：裁剪 → 换算全局 pts → 归一化到基准画布 → 送共享滤镜图。</summary>
        private void ProcessSegmentVideoFrames(AVCodecContext* vDec, ConcatVideoConverter converter, TrimTimeline timeline,
            AVRational segTb, double globalOffsetSec, ref long nextPts, long frameStep)
        {
            while (true)
            {
                _ct.ThrowIfCancellationRequested();
                int decoded = ffmpeg.avcodec_receive_frame(vDec, _decFrame);
                if (decoded is ErrEagain || decoded == ffmpeg.AVERROR_EOF) break;
                if (decoded < 0) throw FFmpegNative.CreateException(decoded, "Receive decoded video frame");
                if (_decFrame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE)
                    _decFrame->pts = _decFrame->best_effort_timestamp;
                if (_decFrame->pts == ffmpeg.AV_NOPTS_VALUE)
                {
                    _decFrame->pts = nextPts;
                    nextPts += frameStep;
                }
                else
                {
                    nextPts = _decFrame->pts + frameStep;
                }
                if (timeline.KeepVideo(_decFrame, segTb, out double position))
                {
                    // KeepVideo 已把 pts 重置为段内相对时间；换算到基准 timebase 的全局时间轴
                    long globalPts = (long)Math.Round((_decFrame->pts * ffmpeg.av_q2d(segTb) + globalOffsetSec) / _refTbSec);
                    _previewConverter.Report(_decFrame, position, _preview);
                    AVFrame* frame = converter.Normalize(_decFrame, globalPts);
                    PushVideoFrame(_srcCtx, _sinkCtx, frame, _filtFrame, _vEnc, _ofmt, _vOutStream, _encPkt, _totalSec, _frameProgress);
                }
                ffmpeg.av_frame_unref(_decFrame);
            }
        }

        /// <summary>排空音频解码器：裁剪 → 首帧平移到全局时间轴 → 段内重采样 → 送共享音频编码器。</summary>
        private void ProcessSegmentAudioFrames(AVCodecContext* aDec, SwrContext* segSwr, TrimTimeline timeline,
            AVRational audioTimeBase, long audioOffsetSamples, ref bool audioOffsetApplied)
        {
            while (ffmpeg.avcodec_receive_frame(aDec, _decFrame) >= 0)
            {
                if (timeline.KeepAudio(_decFrame, audioTimeBase, _audioFeeder!))
                {
                    // KeepAudio 在段内首帧把 feeder 定位到段内起点；随后平移到全局起点
                    if (!audioOffsetApplied)
                    {
                        _audioFeeder!.AddStartSamples(audioOffsetSamples);
                        audioOffsetApplied = true;
                    }
                    ConvertAudioFrame(_decFrame, _swrFrame, _aEnc!, segSwr, _audioFeeder!, _options.AudioGainDb);
                }
                ffmpeg.av_frame_unref(_decFrame);
            }
        }

        /// <summary>全部段解码完成后的一次性冲洗：滤镜图 → 视频编码器 → 音频供料器/编码器 → 封装尾。</summary>
        private void FlushPipeline()
        {
            ffmpeg.av_buffersrc_add_frame(_srcCtx, null);
            DrainSink(_sinkCtx, _filtFrame, _vEnc, _ofmt, _vOutStream, _encPkt, _totalSec, _frameProgress);
            ffmpeg.avcodec_send_frame(_vEnc, null);
            DrainPackets(_vEnc, _ofmt, _vOutStream, _encPkt, _vEnc->time_base);
            if (_aEnc != null)
            {
                _audioFeeder!.Flush();
                ffmpeg.avcodec_send_frame(_aEnc, null);
                DrainPackets(_aEnc, _ofmt, _aOutStream!, _encPkt, _aEnc->time_base);
            }
            ffmpeg.av_write_trailer(_ofmt);
            _previewConverter.Flush(_preview);
            _statistics?.Flush(completed: true);
        }

        public void Dispose() => Cleanup();

        private bool _cleanedUp;

        /// <summary>幂等清理：Run 的 finally 与 Dispose 都会调用，重复执行不得触碰已释放指针。</summary>
        private void Cleanup()
        {
            if (_cleanedUp) return;
            _cleanedUp = true;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? cleanupFailure = null;
            void Release(IDisposable? resource)
            {
                try { resource?.Dispose(); }
                catch (Exception error)
                {
                    cleanupFailure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                    _owner._logger.LogWarning(error, "Video resource cleanup failed");
                }
            }
            Release(_audioFeeder);
            Release(_previewConverter);
            // 释放型 API 需要指针地址；托管字段不能直接取地址，一律经局部变量中转
            AVFrame* swrFrame = _swrFrame;
            ffmpeg.av_frame_free(&swrFrame);
            AVFrame* filtFrame = _filtFrame;
            ffmpeg.av_frame_free(&filtFrame);
            AVFrame* decFrame = _decFrame;
            ffmpeg.av_frame_free(&decFrame);
            AVPacket* decPkt = _decPkt;
            ffmpeg.av_packet_free(&decPkt);
            AVPacket* encPkt = _encPkt;
            ffmpeg.av_packet_free(&encPkt);
            if (_hwFrames != null)
            {
                AVBufferRef* hwFrames = _hwFrames;
                ffmpeg.av_buffer_unref(&hwFrames);
            }
            if (_hwDevice != null)
            {
                AVBufferRef* hwDevice = _hwDevice;
                ffmpeg.av_buffer_unref(&hwDevice);
            }
            if (_graph != null)
            {
                AVFilterGraph* graph = _graph;
                ffmpeg.avfilter_graph_free(&graph);
            }
            if (_aEnc != null)
            {
                AVCodecContext* aEnc = _aEnc;
                ffmpeg.avcodec_free_context(&aEnc);
            }
            if (_vEnc != null)
            {
                AVCodecContext* vEnc = _vEnc;
                ffmpeg.avcodec_free_context(&vEnc);
            }
            if (_ofmt != null)
            {
                if (_outputOpened && _ofmt->pb != null && (_ofmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                    ffmpeg.avio_closep(&_ofmt->pb);
                ffmpeg.avformat_free_context(_ofmt);
            }
            if (!_conversionFailed) cleanupFailure?.Throw();
        }
    }

    /// <summary>把片段解码帧归一化到基准画布：参数与显示宽高比一致时直通，否则 sws 等比缩放居中并补黑边。</summary>
    private sealed unsafe class ConcatVideoConverter : IDisposable
    {
        private readonly int _refWidth;
        private readonly int _refHeight;
        private readonly AVPixelFormat _refFormat;
        private readonly AVRational _refSar;
        private readonly AVColorRange _refRange;
        private readonly byte*[] _srcData = new byte*[8];
        private readonly int[] _srcStride = new int[8];
        private readonly byte*[] _dstData = new byte*[8];
        private readonly int[] _dstStride = new int[8];
        private AVFrame* _canvas;
        private SwsContext* _sws;
        private bool _disposed;

        public ConcatVideoConverter(int refWidth, int refHeight, AVPixelFormat refFormat, AVRational refSar, AVColorRange refRange)
        {
            _refWidth = refWidth;
            _refHeight = refHeight;
            _refFormat = refFormat;
            _refSar = refSar;
            _refRange = refRange;
        }

        /// <summary>返回可直接送入共享 buffersrc 的帧（直通时为源帧本身，转换时为内部画布），时间戳为全局 pts。</summary>
        public AVFrame* Normalize(AVFrame* frame, long globalPts)
        {
            if (frame->width == _refWidth && frame->height == _refHeight && frame->format == (int)_refFormat
                && SameDisplayAspect(frame))
            {
                frame->pts = globalPts;
                frame->duration = ffmpeg.AV_NOPTS_VALUE;
                frame->sample_aspect_ratio = _refSar;
                return frame;
            }

            EnsureCanvas();
            ComputeFitRect(frame, out int dx, out int dy, out int dw, out int dh);
            _sws = ffmpeg.sws_getCachedContext(_sws, frame->width, frame->height, (AVPixelFormat)frame->format,
                dw, dh, _refFormat, (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_sws == null) throw new OutOfMemoryException();
            for (uint i = 0; i < 8; i++)
            {
                _srcData[i] = frame->data[i];
                _srcStride[i] = frame->linesize[i];
            }
            SetRectDestination(dx, dy);
            int ret = ffmpeg.sws_scale(_sws, _srcData, _srcStride, 0, frame->height, _dstData, _dstStride);
            if (ret < 0) throw FFmpegNative.CreateException(ret, "拼接画面归一化失败");
            _canvas->pts = globalPts;
            _canvas->duration = ffmpeg.AV_NOPTS_VALUE;
            _canvas->sample_aspect_ratio = _refSar;
            return _canvas;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_sws != null) ffmpeg.sws_freeContext(_sws);
            if (_canvas != null)
            {
                AVFrame* canvas = _canvas;
                ffmpeg.av_frame_free(&canvas);
            }
        }

        private void EnsureCanvas()
        {
            if (_canvas != null) return;
            _canvas = ffmpeg.av_frame_alloc();
            if (_canvas == null) throw new OutOfMemoryException();
            _canvas->format = (int)_refFormat;
            _canvas->width = _refWidth;
            _canvas->height = _refHeight;
            _canvas->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            if (ffmpeg.av_frame_get_buffer(_canvas, 0) < 0)
                throw new InvalidOperationException("拼接画布缓冲分配失败");
            var data = new byte_ptrArray4();
            var linesizes = new long_array4();
            for (uint i = 0; i < 4; i++)
            {
                data[i] = _canvas->data[i];
                linesizes[i] = _canvas->linesize[i];
            }
            int ret = ffmpeg.av_image_fill_black(ref data, in linesizes, _refFormat, _refRange, _refWidth, _refHeight);
            if (ret < 0) throw FFmpegNative.CreateException(ret, "拼接画布黑底填充失败");
        }

        /// <summary>按源显示宽高比（DAR）在基准画布内取最大内接矩形；子采样格式强制偶数尺寸。</summary>
        private void ComputeFitRect(AVFrame* frame, out int dx, out int dy, out int dw, out int dh)
        {
            double srcSar = SarValue(frame->sample_aspect_ratio);
            double refSar = SarValue(_refSar);
            double fit = Math.Min(
                _refWidth * refSar / (frame->width * srcSar),
                (double)_refHeight / frame->height);
            dw = Math.Clamp((int)Math.Round(frame->width * fit * srcSar / refSar), 2, _refWidth);
            dh = Math.Clamp((int)Math.Round(frame->height * fit), 2, _refHeight);
            var descriptor = ffmpeg.av_pix_fmt_desc_get(_refFormat);
            if (descriptor != null && descriptor->log2_chroma_w > 0) dw -= dw & 1;
            if (descriptor != null && descriptor->log2_chroma_h > 0) dh -= dh & 1;
            dx = (_refWidth - dw) / 2;
            dy = (_refHeight - dh) / 2;
        }

        /// <summary>让 sws 直接写入画布上目标矩形的各平面位置（按子采样与每像素字节数换算偏移）。</summary>
        private void SetRectDestination(int dx, int dy)
        {
            var descriptor = ffmpeg.av_pix_fmt_desc_get(_refFormat);
            if (descriptor == null) throw new InvalidOperationException("未知像素格式描述符");
            for (uint plane = 0; plane < 4; plane++)
            {
                byte* planeData = _canvas->data[plane];
                if (planeData == null) { _dstData[plane] = null; _dstStride[plane] = 0; continue; }
                bool lumaOrAlpha = plane == 0 || plane == 3;
                int shiftX = lumaOrAlpha ? 0 : descriptor->log2_chroma_w;
                int shiftY = lumaOrAlpha ? 0 : descriptor->log2_chroma_h;
                int step = PlanePixelStep(descriptor, (int)plane);
                _dstData[plane] = planeData + (long)(dy >> shiftY) * _canvas->linesize[plane] + (long)(dx >> shiftX) * step;
                _dstStride[plane] = _canvas->linesize[plane];
            }
        }

        private bool SameDisplayAspect(AVFrame* frame)
        {
            double a = (double)frame->width * SarValue(frame->sample_aspect_ratio) / frame->height;
            double b = (double)_refWidth * SarValue(_refSar) / _refHeight;
            return Math.Abs(a - b) <= 0.01 * Math.Max(1.0, Math.Min(a, b));
        }

        private static double SarValue(AVRational sar)
            => sar.num > 0 && sar.den > 0 ? (double)sar.num / sar.den : 1.0;

        private static int PlanePixelStep(AVPixFmtDescriptor* descriptor, int plane)
        {
            int step = 1;
            for (uint i = 0; i < 4; i++)
            {
                var component = descriptor->comp[i];
                if (component.plane == plane && component.step > step) step = component.step;
            }
            return step;
        }
    }
}
