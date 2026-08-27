using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using FancyToolAva.Models;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

public sealed class VideoTranscodeService : IVideoTranscodeService
{
    private readonly ILogger<VideoTranscodeService> _logger;
    private readonly IFfmpegService _ffmpeg;

    public VideoTranscodeService(ILogger<VideoTranscodeService> logger, IFfmpegService ffmpeg)
    {
        _logger = logger;
        _ffmpeg = ffmpeg;
    }

    private void EnsureAvailable()
    {
        if (!_ffmpeg.IsAvailable || string.IsNullOrWhiteSpace(_ffmpeg.ResolvedDirectory))
            throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegNotAvailable"));
        string? dir = _ffmpeg.ResolvedDirectory;
        if (!FfmpegNativeLoader.IsInitialized)
        {
            if (!FfmpegNativeLoader.TryInitialize(dir!, _logger, out string? err))
                throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_FfmpegProbeFailed", err ?? "init failed"));
        }
    }

    public async Task<VideoProbeInfo?> ProbeAsync(string inputPath, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException(inputPath);
        EnsureAvailable();
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            unsafe { return ProbeNative(inputPath); }
        }, ct).ConfigureAwait(false);
    }

    private unsafe VideoProbeInfo? ProbeNative(string input)
    {
        AVFormatContext* fmtCtx = null;
        try
        {
            int ret = ffmpeg.avformat_open_input(&fmtCtx, input, null, null);
            if (ret < 0) throw new InvalidOperationException($"avformat_open_input failed: {FfmpegNativeLoader.GetErrorString(ret)}");
            ret = ffmpeg.avformat_find_stream_info(fmtCtx, null);
            if (ret < 0) throw new InvalidOperationException($"avformat_find_stream_info failed: {FfmpegNativeLoader.GetErrorString(ret)}");

            long durationMs = 0;
            if (fmtCtx->duration != ffmpeg.AV_NOPTS_VALUE)
                durationMs = fmtCtx->duration / 1000;
            long bitRate = fmtCtx->bit_rate;
            string container = "";
            if (fmtCtx->iformat != null && fmtCtx->iformat->name != null)
                container = Marshal.PtrToStringAnsi((IntPtr)fmtCtx->iformat->name) ?? "";

            int width = 0, height = 0;
            double fps = 0;
            string vCodec = "", aCodec = "";
            bool hasVideo = false, hasAudio = false;

            for (int i = 0; i < fmtCtx->nb_streams; i++)
            {
                AVStream* st = fmtCtx->streams[i];
                AVCodecParameters* par = st->codecpar;
                if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && !hasVideo)
                {
                    hasVideo = true;
                    vCodec = GetCodecName(par->codec_id);
                    width = par->width;
                    height = par->height;
                    AVRational fr = st->avg_frame_rate;
                    if (fr.num == 0 || fr.den == 0) fr = st->r_frame_rate;
                    if (fr.num != 0 && fr.den != 0) fps = ffmpeg.av_q2d(fr);
                    if (durationMs == 0 && st->duration != ffmpeg.AV_NOPTS_VALUE)
                        durationMs = (long)(st->duration * ffmpeg.av_q2d(st->time_base) * 1000);
                }
                else if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && !hasAudio)
                {
                    hasAudio = true;
                    aCodec = GetCodecName(par->codec_id);
                }
            }
            if (string.IsNullOrEmpty(container))
            {
                var ext = Path.GetExtension(input);
                if (!string.IsNullOrEmpty(ext)) container = ext.TrimStart('.');
            }
            return new VideoProbeInfo(input, durationMs, width, height, fps, vCodec, aCodec, container, bitRate, hasAudio, hasVideo);
        }
        finally
        {
            if (fmtCtx != null) ffmpeg.avformat_close_input(&fmtCtx);
        }
    }

    private static unsafe string GetCodecName(AVCodecID id)
    {
        AVCodec* c = ffmpeg.avcodec_find_decoder(id);
        if (c != null && c->name != null) return Marshal.PtrToStringAnsi((IntPtr)c->name) ?? id.ToString();
        c = ffmpeg.avcodec_find_encoder(id);
        if (c != null && c->name != null) return Marshal.PtrToStringAnsi((IntPtr)c->name) ?? id.ToString();
        return id.ToString();
    }

    public async Task TranscodeAsync(string inputPath, string outputPath, VideoTranscodeOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException(inputPath);
        EnsureAvailable();
        ct.ThrowIfCancellationRequested();
        long durationMs = 0;
        try
        {
            var probe = await ProbeAsync(inputPath, ct).ConfigureAwait(false);
            durationMs = probe?.DurationMs ?? 0;
        }
        catch { }

        string tmpPath = BuildTmpPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        bool useTwoPass = options.TwoPassEnabled && options.RateControl == RateControlMode.Bitrate && options.Container != VideoContainer.Gif && options.HardwareBackend == HardwareBackend.Software;
        if (useTwoPass)
        {
            string passLog = Path.Combine(Path.GetTempPath(), $"ffmpeg2pass_{Guid.NewGuid():N}");
            try
            {
                _logger.LogInformation("Native 2-pass pass1 for {Input}", inputPath);
                await Task.Run(() =>
                {
                    unsafe { TranscodeNative(inputPath, tmpPath + ".pass1.tmp", options, 1, passLog, durationMs, new ScaledProgress(progress, 0, 0.5), ct); }
                }, ct).ConfigureAwait(false);
                if (File.Exists(tmpPath + ".pass1.tmp")) File.Delete(tmpPath + ".pass1.tmp");
                _logger.LogInformation("Native 2-pass pass2 for {Input}", inputPath);
                await Task.Run(() =>
                {
                    unsafe { TranscodeNative(inputPath, tmpPath, options, 2, passLog, durationMs, new ScaledProgress(progress, 0.5, 0.5), ct); }
                }, ct).ConfigureAwait(false);
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tmpPath, outputPath);
                progress?.Report(1.0);
                _logger.LogInformation("Transcode 2-pass done: {Input} -> {Output}", inputPath, outputPath);
            }
            finally
            {
                TryDelete(passLog);
                TryDelete(passLog + "-0.log");
                TryDelete(passLog + "-0.log.mbtree");
                TryDelete(tmpPath + ".pass1.tmp");
                try { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(passLog) + "*")) TryDelete(f); } catch { }
            }
            return;
        }

        await Task.Run(() =>
        {
            unsafe { TranscodeNative(inputPath, tmpPath, options, null, null, durationMs, progress, ct); }
        }, ct).ConfigureAwait(false);
        if (File.Exists(outputPath)) File.Delete(outputPath);
        try { File.Move(tmpPath, outputPath); } catch { TryDelete(tmpPath); throw; }
        progress?.Report(1.0);
        _logger.LogInformation("Transcode done: {Input} -> {Output}", inputPath, outputPath);
    }

    private unsafe void TranscodeNative(string inputPath, string outputPath, VideoTranscodeOptions opts, int? pass, string? passLogFile, long durationMs, IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string videoEncoderName = MapVideoEncoderName(opts);
        string audioEncoderName = MapAudioEncoderName(opts.AudioCodec);

        AVFormatContext* ifmtCtx = null;
        AVFormatContext* ofmtCtx = null;
        AVCodecContext* vDecCtx = null;
        AVCodecContext* aDecCtx = null;
        AVCodecContext* vEncCtx = null;
        AVCodecContext* aEncCtx = null;
        AVFilterGraph* vFilterGraph = null;
        AVFilterContext* vFilterSrc = null;
        AVFilterContext* vFilterSink = null;
        SwrContext* swrCtx = null;
        AVBufferRef* hwDeviceCtx = null;

        AVPacket* pkt = null;
        AVFrame* frame = null;
        AVFrame* filtFrame = null;
        AVFrame* swrFrame = null;

        int vStreamIdx = -1, aStreamIdx = -1;
        AVStream* vInStream = null;
        AVStream* aInStream = null;
        AVStream* vOutStream = null;
        AVStream* aOutStream = null;

        try
        {
            int ret = ffmpeg.avformat_open_input(&ifmtCtx, inputPath, null, null);
            if (ret < 0) throw new InvalidOperationException($"avformat_open_input: {FfmpegNativeLoader.GetErrorString(ret)}");
            ret = ffmpeg.avformat_find_stream_info(ifmtCtx, null);
            if (ret < 0) throw new InvalidOperationException($"avformat_find_stream_info: {FfmpegNativeLoader.GetErrorString(ret)}");

            vStreamIdx = ffmpeg.av_find_best_stream(ifmtCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            aStreamIdx = ffmpeg.av_find_best_stream(ifmtCtx, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (vStreamIdx >= 0) vInStream = ifmtCtx->streams[vStreamIdx];
            if (aStreamIdx >= 0) aInStream = ifmtCtx->streams[aStreamIdx];

            bool hasVideo = vStreamIdx >= 0 && vInStream != null;
            bool hasAudio = aStreamIdx >= 0 && aInStream != null && opts.AudioCodec != AudioCodec.None && opts.Container != VideoContainer.Gif;
            if (opts.Container == VideoContainer.Gif) hasAudio = false;

            if (hasVideo)
            {
                AVCodec* dec = ffmpeg.avcodec_find_decoder(vInStream->codecpar->codec_id);
                if (dec == null) throw new InvalidOperationException($"Video decoder not found for {vInStream->codecpar->codec_id}");
                vDecCtx = ffmpeg.avcodec_alloc_context3(dec);
                if (vDecCtx == null) throw new InvalidOperationException("avcodec_alloc_context3 vDec failed");
                ret = ffmpeg.avcodec_parameters_to_context(vDecCtx, vInStream->codecpar);
                if (ret < 0) throw new InvalidOperationException($"avcodec_parameters_to_context vDec: {FfmpegNativeLoader.GetErrorString(ret)}");
                vDecCtx->pkt_timebase = vInStream->time_base;
                if (IsVaapiBackend(opts.HardwareBackend))
                {
                    ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, "/dev/dri/renderD128", null, 0);
                    if (ret == 0 && hwDeviceCtx != null)
                        vDecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
                    else
                    {
                        _logger.LogWarning("VAAPI device creation failed: {Err}", FfmpegNativeLoader.GetErrorString(ret));
                        if (hwDeviceCtx != null) ffmpeg.av_buffer_unref(&hwDeviceCtx);
                    }
                }
                ret = ffmpeg.avcodec_open2(vDecCtx, dec, null);
                if (ret < 0) throw new InvalidOperationException($"avcodec_open2 vDec: {FfmpegNativeLoader.GetErrorString(ret)}");
            }

            if (hasAudio)
            {
                AVCodec* dec = ffmpeg.avcodec_find_decoder(aInStream->codecpar->codec_id);
                if (dec != null)
                {
                    aDecCtx = ffmpeg.avcodec_alloc_context3(dec);
                    if (aDecCtx != null)
                    {
                        ret = ffmpeg.avcodec_parameters_to_context(aDecCtx, aInStream->codecpar);
                        if (ret >= 0)
                        {
                            aDecCtx->pkt_timebase = aInStream->time_base;
                            ret = ffmpeg.avcodec_open2(aDecCtx, dec, null);
                            if (ret < 0)
                            {
                                _logger.LogWarning("Failed to open audio decoder, skip audio: {Err}", FfmpegNativeLoader.GetErrorString(ret));
                                ffmpeg.avcodec_free_context(&aDecCtx);
                                hasAudio = false;
                            }
                        }
                        else
                        {
                            ffmpeg.avcodec_free_context(&aDecCtx);
                            hasAudio = false;
                        }
                    }
                }
                else hasAudio = false;
            }

            string formatName = GetFormatForContainer(opts.Container);
            ret = ffmpeg.avformat_alloc_output_context2(&ofmtCtx, null, formatName, outputPath);
            if (ret < 0 || ofmtCtx == null) throw new InvalidOperationException($"avformat_alloc_output_context2 failed: {FfmpegNativeLoader.GetErrorString(ret)}");

            if (hasVideo)
            {
                AVCodec* enc = null;
                if (opts.HardwareBackend != HardwareBackend.Software)
                {
                    string hwName = MapVideoEncoderName(opts);
                    enc = ffmpeg.avcodec_find_encoder_by_name(hwName);
                    if (enc == null) _logger.LogWarning("HW encoder {Hw} not found, fallback to software", hwName);
                }
                if (enc == null)
                {
                    string swName = MapSoftwareVideoEncoderName(opts.VideoCodec);
                    enc = ffmpeg.avcodec_find_encoder_by_name(swName);
                }
                if (enc == null)
                {
                    AVCodecID id = MapVideoCodecId(opts.VideoCodec);
                    enc = ffmpeg.avcodec_find_encoder(id);
                }
                if (enc == null) throw new InvalidOperationException($"Video encoder not found for {videoEncoderName}");
                vEncCtx = ffmpeg.avcodec_alloc_context3(enc);
                if (vEncCtx == null) throw new InvalidOperationException("avcodec_alloc_context3 vEnc failed");
                // Formats with AVFMT_GLOBALHEADER (mp4/mkv/mov) require codec extradata at header time;
                // libx264/libx265 only generate it when AV_CODEC_FLAG_GLOBAL_HEADER is set (mirrors ffmpeg CLI).
                if ((ofmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                    vEncCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

                int outW = vDecCtx->width;
                int outH = vDecCtx->height;
                (outW, outH) = CalculateOutputSize(vDecCtx->width, vDecCtx->height, opts);
                if (opts.Container != VideoContainer.Gif)
                {
                    outW &= ~1; outH &= ~1;
                    if (outW < 16) outW = 16;
                    if (outH < 16) outH = 16;
                }
                vEncCtx->width = outW;
                vEncCtx->height = outH;
                vEncCtx->sample_aspect_ratio = vDecCtx->sample_aspect_ratio;
                AVRational outTimeBase;
                if (opts.FpsMode != FpsMode.SameAsSource && opts.FpsValue > 0)
                {
                    int fpsInt = (int)Math.Round(opts.FpsValue);
                    if (Math.Abs(opts.FpsValue - fpsInt) < 0.001)
                        outTimeBase = new AVRational { num = 1, den = fpsInt };
                    else
                        outTimeBase = new AVRational { num = 1000, den = (int)Math.Round(opts.FpsValue * 1000) };
                }
                else
                {
                    AVRational fr = vInStream->avg_frame_rate;
                    if (fr.num == 0 || fr.den == 0) fr = vInStream->r_frame_rate;
                    if (fr.num == 0 || fr.den == 0) fr = new AVRational { num = 30, den = 1 };
                    outTimeBase = new AVRational { num = fr.den, den = fr.num };
                }
                vEncCtx->time_base = outTimeBase;

                if (opts.Container == VideoContainer.Gif)
                    vEncCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_PAL8;
                else if (IsVaapiBackend(opts.HardwareBackend))
                {
                    vEncCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_VAAPI;
                    if (hwDeviceCtx != null) vEncCtx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
                }
                else if (IsQsvBackend(opts.HardwareBackend))
                {
                    // QSV works with system-memory NV12 frames (no hw_device/hw_frames needed):
                    // verified against ffmpeg CLI: -c:v h264_qsv/hevc_qsv -pix_fmt nv12 succeeds,
                    // while explicit -init_hw_device qsv + hwupload fails with Invalid argument.
                    vEncCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
                }
                else
                    vEncCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;

                if (opts.FpsMode == FpsMode.Fixed || opts.FpsMode == FpsMode.Peak)
                    vEncCtx->framerate = new AVRational { num = (int)Math.Round(opts.FpsValue * 1000), den = 1000 };
                else
                {
                    AVRational fr2 = vInStream->avg_frame_rate;
                    if (fr2.num == 0 || fr2.den == 0) fr2 = vInStream->r_frame_rate;
                    if (fr2.num != 0 && fr2.den != 0) vEncCtx->framerate = fr2;
                }
                // SVT-AV1 hard-requires framerate; fall back to 30fps when source has none (mirrors ffmpeg CLI)
                if (vEncCtx->framerate.num <= 0 || vEncCtx->framerate.den <= 0)
                    vEncCtx->framerate = new AVRational { num = 30, den = 1 };

                AVDictionary* optsDict = null;
                try
                {
                    var (presetKey, presetVal, presetIsInt) = MapPresetOption(opts);
                    if (opts.RateControl == RateControlMode.Crf)
                    {
                        string crfVal = opts.Crf.ToString();
                        if (presetKey != null) SetCodecOption(vEncCtx, &optsDict, presetKey, presetVal, presetIsInt);
                        if (opts.HardwareBackend == HardwareBackend.Software)
                        {
                            if (opts.VideoCodec == VideoCodec.H264 || opts.VideoCodec == VideoCodec.H265 || opts.VideoCodec == VideoCodec.Av1Aom || opts.VideoCodec == VideoCodec.Av1Svt || opts.VideoCodec == VideoCodec.Vp8 || opts.VideoCodec == VideoCodec.Vp9)
                            {
                                SetCodecOption(vEncCtx, &optsDict, "crf", crfVal, false);
                                if (opts.VideoCodec == VideoCodec.Vp8 || opts.VideoCodec == VideoCodec.Vp9 || opts.VideoCodec == VideoCodec.Av1Aom || opts.VideoCodec == VideoCodec.Av1Svt)
                                    vEncCtx->bit_rate = 0;
                            }
                        }
                        else
                        {
                            if (opts.HardwareBackend == HardwareBackend.Nvidia)
                            {
                                SetCodecOption(vEncCtx, &optsDict, "qp", crfVal, false);
                                SetCodecOption(vEncCtx, &optsDict, "rc", "constqp", false);
                            }
                            else if (opts.HardwareBackend == HardwareBackend.Intel && !IsVaapiBackend(opts.HardwareBackend))
                                SetCodecOption(vEncCtx, &optsDict, "global_quality", crfVal, false);
                            else if (opts.HardwareBackend == HardwareBackend.Amd && !IsVaapiBackend(opts.HardwareBackend))
                            {
                                SetCodecOption(vEncCtx, &optsDict, "qp_i", crfVal, false);
                                SetCodecOption(vEncCtx, &optsDict, "qp_p", crfVal, false);
                            }
                            else if (IsVaapiBackend(opts.HardwareBackend))
                                SetCodecOption(vEncCtx, &optsDict, "qp", crfVal, false);
                        }
                    }
                    else
                    {
                        vEncCtx->bit_rate = (long)opts.VideoBitrateKbps * 1000;
                        if (opts.VideoCodec == VideoCodec.H264 || opts.VideoCodec == VideoCodec.H265)
                        {
                            vEncCtx->rc_max_rate = vEncCtx->bit_rate;
                            vEncCtx->rc_buffer_size = (int)(vEncCtx->bit_rate * 2);
                        }
                        if (presetKey != null) SetCodecOption(vEncCtx, &optsDict, presetKey, presetVal, presetIsInt);
                        if (pass != null && passLogFile != null)
                        {
                            SetCodecOption(vEncCtx, &optsDict, "pass", pass.Value.ToString(), false);
                            SetCodecOption(vEncCtx, &optsDict, "passlogfile", passLogFile, false);
                        }
                    }
                    if (!string.IsNullOrEmpty(opts.Profile)) SetCodecOption(vEncCtx, &optsDict, "profile", opts.Profile, false);
                    if (!string.IsNullOrEmpty(opts.Level)) SetCodecOption(vEncCtx, &optsDict, "level", opts.Level, false);
                    if (opts.VideoCodec == VideoCodec.Vp8 || opts.VideoCodec == VideoCodec.Vp9)
                    {
                        SetCodecOption(vEncCtx, &optsDict, "auto-alt-ref", "1", false);
                        SetCodecOption(vEncCtx, &optsDict, "lag-in-frames", "25", false);
                    }
                    ret = ffmpeg.avcodec_open2(vEncCtx, enc, &optsDict);
                    if (ret < 0) throw new InvalidOperationException($"avcodec_open2 vEnc ({Marshal.PtrToStringAnsi((IntPtr)enc->name)}) failed: {FfmpegNativeLoader.GetErrorString(ret)}");
                }
                finally
                {
                    if (optsDict != null) ffmpeg.av_dict_free(&optsDict);
                }

                vOutStream = ffmpeg.avformat_new_stream(ofmtCtx, null);
                if (vOutStream == null) throw new InvalidOperationException("avformat_new_stream vOut failed");
                ret = ffmpeg.avcodec_parameters_from_context(vOutStream->codecpar, vEncCtx);
                if (ret < 0) throw new InvalidOperationException($"avcodec_parameters_from_context vOut: {FfmpegNativeLoader.GetErrorString(ret)}");
                vOutStream->time_base = vEncCtx->time_base;
                vOutStream->avg_frame_rate = vEncCtx->framerate;
                vOutStream->r_frame_rate = vEncCtx->framerate;
            }

            if (hasAudio && aDecCtx != null)
            {
                AVCodec* aEnc = ffmpeg.avcodec_find_encoder_by_name(audioEncoderName);
                if (aEnc == null) aEnc = ffmpeg.avcodec_find_encoder_by_name("aac");
                if (aEnc == null) throw new InvalidOperationException($"Audio encoder {audioEncoderName} not found");
                aEncCtx = ffmpeg.avcodec_alloc_context3(aEnc);
                if (aEncCtx == null) throw new InvalidOperationException("avcodec_alloc_context3 aEnc failed");
                if ((ofmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                    aEncCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                aEncCtx->sample_rate = aDecCtx->sample_rate;
                if (aEncCtx->sample_rate == 0) aEncCtx->sample_rate = 48000;
                ret = ffmpeg.av_channel_layout_copy(&aEncCtx->ch_layout, &aDecCtx->ch_layout);
                if (ret < 0) ffmpeg.av_channel_layout_default(&aEncCtx->ch_layout, 2);
                aEncCtx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                aEncCtx->time_base = new AVRational { num = 1, den = aEncCtx->sample_rate };
                aEncCtx->bit_rate = (long)opts.AudioBitrateKbps * 1000;
                AVDictionary* aDict = null;
                ret = ffmpeg.avcodec_open2(aEncCtx, aEnc, &aDict);
                if (aDict != null) ffmpeg.av_dict_free(&aDict);
                if (ret < 0) throw new InvalidOperationException($"avcodec_open2 aEnc: {FfmpegNativeLoader.GetErrorString(ret)}");
                aOutStream = ffmpeg.avformat_new_stream(ofmtCtx, null);
                if (aOutStream == null) throw new InvalidOperationException("avformat_new_stream aOut failed");
                ret = ffmpeg.avcodec_parameters_from_context(aOutStream->codecpar, aEncCtx);
                if (ret < 0) throw new InvalidOperationException($"avcodec_parameters_from_context aOut: {FfmpegNativeLoader.GetErrorString(ret)}");
                aOutStream->time_base = aEncCtx->time_base;

                bool needSwr = aDecCtx->sample_rate != aEncCtx->sample_rate ||
                               aDecCtx->sample_fmt != aEncCtx->sample_fmt ||
                               ffmpeg.av_channel_layout_compare(&aDecCtx->ch_layout, &aEncCtx->ch_layout) != 0;
                if (needSwr)
                {
                    ret = ffmpeg.swr_alloc_set_opts2(&swrCtx, &aEncCtx->ch_layout, aEncCtx->sample_fmt, aEncCtx->sample_rate,
                        &aDecCtx->ch_layout, aDecCtx->sample_fmt, aDecCtx->sample_rate, 0, null);
                    if (ret < 0) throw new InvalidOperationException($"swr_alloc_set_opts2: {FfmpegNativeLoader.GetErrorString(ret)}");
                    ret = ffmpeg.swr_init(swrCtx);
                    if (ret < 0) throw new InvalidOperationException($"swr_init: {FfmpegNativeLoader.GetErrorString(ret)}");
                }
            }

            bool needVideoFilter = hasVideo && vDecCtx != null && vEncCtx != null;
            bool hasFilter = needVideoFilter && (opts.CropEnabled || opts.Deinterlace != DeinterlaceMode.None || opts.Denoise != DenoiseMode.None || opts.ScaleMode != ScaleMode.None || opts.FpsMode != FpsMode.SameAsSource || IsVaapiBackend(opts.HardwareBackend) || IsQsvBackend(opts.HardwareBackend));
            if (opts.Container == VideoContainer.Gif) hasFilter = true;

            if (needVideoFilter)
            {
                if (opts.Container == VideoContainer.Gif)
                {
                    string gifFilter = BuildGifFilterString(opts);
                    ret = CreateVideoFilterGraph(vDecCtx, vEncCtx, gifFilter, &vFilterGraph, &vFilterSrc, &vFilterSink);
                    if (ret < 0) throw new InvalidOperationException($"Create GIF filter graph failed: {FfmpegNativeLoader.GetErrorString(ret)}");
                }
                else if (hasFilter)
                {
                    string filtDesc = BuildVideoFilterString(opts);
                    ret = CreateVideoFilterGraph(vDecCtx, vEncCtx, filtDesc, &vFilterGraph, &vFilterSrc, &vFilterSink);
                    if (ret < 0)
                    {
                        _logger.LogWarning("Video filter graph creation failed, fallback without filter: {Err}", FfmpegNativeLoader.GetErrorString(ret));
                        hasFilter = false;
                    }
                }
                else if (vEncCtx->width != vDecCtx->width || vEncCtx->height != vDecCtx->height)
                {
                    string scaleFilt = $"scale={vEncCtx->width}:{vEncCtx->height}:flags=lanczos";
                    ret = CreateVideoFilterGraph(vDecCtx, vEncCtx, scaleFilt, &vFilterGraph, &vFilterSrc, &vFilterSink);
                    if (ret < 0) _logger.LogWarning("Scale filter failed: {Err}", FfmpegNativeLoader.GetErrorString(ret));
                }
            }

            if ((ofmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ret = ffmpeg.avio_open(&ofmtCtx->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE);
                if (ret < 0) throw new InvalidOperationException($"avio_open {outputPath}: {FfmpegNativeLoader.GetErrorString(ret)}");
            }
            ret = ffmpeg.avformat_write_header(ofmtCtx, null);
            if (ret < 0) throw new InvalidOperationException($"avformat_write_header: {FfmpegNativeLoader.GetErrorString(ret)}");

            pkt = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            filtFrame = ffmpeg.av_frame_alloc();
            if (swrCtx != null) swrFrame = ffmpeg.av_frame_alloc();
            if (pkt == null || frame == null || filtFrame == null) throw new InvalidOperationException("av_frame/packet alloc failed");

            long totalFrames = 0;
            long processedFrames = 0;
            long lastVideoPts = ffmpeg.AV_NOPTS_VALUE;
            long lastVideoDts = ffmpeg.AV_NOPTS_VALUE;
            long nextAudioPts = 0;
            long lastAudioDts = ffmpeg.AV_NOPTS_VALUE;
            double srcFps = 30;
            if (hasVideo && vInStream != null)
            {
                AVRational fr = vInStream->avg_frame_rate;
                if (fr.num == 0 || fr.den == 0) fr = vInStream->r_frame_rate;
                if (fr.num != 0 && fr.den != 0) srcFps = ffmpeg.av_q2d(fr);
            }
            if (durationMs > 0) totalFrames = (long)(durationMs / 1000.0 * srcFps);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                ret = ffmpeg.av_read_frame(ifmtCtx, pkt);
                if (ret == ffmpeg.AVERROR_EOF) break;
                if (ret < 0) throw new InvalidOperationException($"av_read_frame: {FfmpegNativeLoader.GetErrorString(ret)}");

                if (pkt->stream_index == vStreamIdx && hasVideo)
                {
                    ret = ffmpeg.avcodec_send_packet(vDecCtx, pkt);
                    if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN)) { ffmpeg.av_packet_unref(pkt); continue; }
                    while (ret >= 0)
                    {
                        ret = ffmpeg.avcodec_receive_frame(vDecCtx, frame);
                        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                        if (ret < 0) throw new InvalidOperationException($"avcodec_receive_frame video: {FfmpegNativeLoader.GetErrorString(ret)}");
                        AVFrame* srcFrame = frame;
                        if (vFilterGraph != null)
                        {
                            ret = ffmpeg.av_buffersrc_add_frame_flags(vFilterSrc, frame, 1);
                            if (ret < 0) throw new InvalidOperationException($"av_buffersrc_add_frame: {FfmpegNativeLoader.GetErrorString(ret)}");
                            ret = ffmpeg.av_buffersink_get_frame(vFilterSink, filtFrame);
                            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) { ffmpeg.av_frame_unref(frame); continue; }
                            if (ret < 0) throw new InvalidOperationException($"av_buffersink_get_frame: {FfmpegNativeLoader.GetErrorString(ret)}");
                            srcFrame = filtFrame;
                        }
                        srcFrame->pts = MonotonicVideoPts(srcFrame->pts, ref lastVideoPts,
                            vFilterGraph != null ? ffmpeg.av_buffersink_get_time_base(vFilterSink) : vInStream->time_base,
                            vEncCtx->time_base, vEncCtx->framerate);
                        ret = ffmpeg.avcodec_send_frame(vEncCtx, srcFrame);
                        if (ret < 0) throw new InvalidOperationException($"avcodec_send_frame vEnc: {FfmpegNativeLoader.GetErrorString(ret)}");
                        while (true)
                        {
                            AVPacket* encPkt = ffmpeg.av_packet_alloc();
                            ret = ffmpeg.avcodec_receive_packet(vEncCtx, encPkt);
                            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) { ffmpeg.av_packet_free(&encPkt); break; }
                            if (ret < 0) { ffmpeg.av_packet_free(&encPkt); throw new InvalidOperationException($"avcodec_receive_packet vEnc: {FfmpegNativeLoader.GetErrorString(ret)}"); }
                            ffmpeg.av_packet_rescale_ts(encPkt, vEncCtx->time_base, vOutStream->time_base);
                            FixPacketDts(encPkt, ref lastVideoDts);
                            encPkt->stream_index = vOutStream->index;
                            ret = ffmpeg.av_interleaved_write_frame(ofmtCtx, encPkt);
                            ffmpeg.av_packet_free(&encPkt);
                            if (ret < 0) throw new InvalidOperationException($"av_interleaved_write_frame: {FfmpegNativeLoader.GetErrorString(ret)}");
                        }
                        if (vFilterGraph != null) ffmpeg.av_frame_unref(filtFrame);
                        ffmpeg.av_frame_unref(frame);
                        processedFrames++;
                        if (progress != null && durationMs > 0)
                        {
                            long ptsMs = (long)(srcFrame->pts * ffmpeg.av_q2d(vEncCtx->time_base) * 1000);
                            if (ptsMs > 0) progress.Report(Math.Clamp((double)ptsMs / durationMs, 0, 1));
                            else if (totalFrames > 0) progress.Report(Math.Clamp((double)processedFrames / totalFrames, 0, 1));
                        }
                        else if (progress != null && totalFrames > 0) progress.Report(Math.Clamp((double)processedFrames / totalFrames, 0, 1));
                    }
                }
                else if (pkt->stream_index == aStreamIdx && hasAudio && aDecCtx != null && aEncCtx != null)
                {
                    ret = ffmpeg.avcodec_send_packet(aDecCtx, pkt);
                    if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN)) { ffmpeg.av_packet_unref(pkt); continue; }
                    while (ret >= 0)
                    {
                        ret = ffmpeg.avcodec_receive_frame(aDecCtx, frame);
                        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                        if (ret < 0) throw new InvalidOperationException($"avcodec_receive_frame audio: {FfmpegNativeLoader.GetErrorString(ret)}");
                        AVFrame* encFrame = frame;
                        if (swrCtx != null)
                        {
                            if (swrFrame == null) swrFrame = ffmpeg.av_frame_alloc();
                            swrFrame->sample_rate = aEncCtx->sample_rate;
                            ffmpeg.av_channel_layout_copy(&swrFrame->ch_layout, &aEncCtx->ch_layout);
                            swrFrame->format = (int)aEncCtx->sample_fmt;
                            swrFrame->pts = frame->pts;
                            ret = ffmpeg.swr_convert_frame(swrCtx, swrFrame, frame);
                            if (ret < 0) throw new InvalidOperationException($"swr_convert_frame: {FfmpegNativeLoader.GetErrorString(ret)}");
                            encFrame = swrFrame;
                        }
                        if (encFrame->pts != ffmpeg.AV_NOPTS_VALUE)
                            encFrame->pts = ffmpeg.av_rescale_q(encFrame->pts, aInStream->time_base, aEncCtx->time_base);
                        if (encFrame->pts == ffmpeg.AV_NOPTS_VALUE || encFrame->pts < nextAudioPts)
                            encFrame->pts = nextAudioPts;
                        ret = ffmpeg.avcodec_send_frame(aEncCtx, encFrame);
                        if (ret < 0) throw new InvalidOperationException($"avcodec_send_frame aEnc: {FfmpegNativeLoader.GetErrorString(ret)}");
                        while (true)
                        {
                            AVPacket* encPkt = ffmpeg.av_packet_alloc();
                            ret = ffmpeg.avcodec_receive_packet(aEncCtx, encPkt);
                            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) { ffmpeg.av_packet_free(&encPkt); break; }
                            if (ret < 0) { ffmpeg.av_packet_free(&encPkt); throw new InvalidOperationException($"avcodec_receive_packet aEnc: {FfmpegNativeLoader.GetErrorString(ret)}"); }
                            ffmpeg.av_packet_rescale_ts(encPkt, aEncCtx->time_base, aOutStream->time_base);
                            FixPacketDts(encPkt, ref lastAudioDts);
                            encPkt->stream_index = aOutStream->index;
                            ret = ffmpeg.av_interleaved_write_frame(ofmtCtx, encPkt);
                            ffmpeg.av_packet_free(&encPkt);
                            if (ret < 0) throw new InvalidOperationException($"av_interleaved_write_frame audio: {FfmpegNativeLoader.GetErrorString(ret)}");
                        }
                        if (encFrame->pts >= nextAudioPts)
                            nextAudioPts = encFrame->pts + encFrame->nb_samples;
                        if (swrCtx != null) ffmpeg.av_frame_unref(swrFrame);
                        ffmpeg.av_frame_unref(frame);
                    }
                }
                ffmpeg.av_packet_unref(pkt);
            }

            // Flush decoder
            if (hasVideo && vDecCtx != null)
            {
                ffmpeg.avcodec_send_packet(vDecCtx, null);
                while (ffmpeg.avcodec_receive_frame(vDecCtx, frame) == 0)
                {
                    AVFrame* srcFrame = frame;
                    if (vFilterGraph != null)
                    {
                        ffmpeg.av_buffersrc_add_frame_flags(vFilterSrc, frame, 1);
                        while (ffmpeg.av_buffersink_get_frame(vFilterSink, filtFrame) == 0)
                        {
                            srcFrame = filtFrame;
                            srcFrame->pts = MonotonicVideoPts(srcFrame->pts, ref lastVideoPts, ffmpeg.av_buffersink_get_time_base(vFilterSink), vEncCtx->time_base, vEncCtx->framerate);
                            ffmpeg.avcodec_send_frame(vEncCtx, srcFrame);
                            AVPacket* encPkt = ffmpeg.av_packet_alloc();
                            while (ffmpeg.avcodec_receive_packet(vEncCtx, encPkt) == 0)
                            {
                                ffmpeg.av_packet_rescale_ts(encPkt, vEncCtx->time_base, vOutStream->time_base);
                                FixPacketDts(encPkt, ref lastVideoDts);
                                encPkt->stream_index = vOutStream->index;
                                ffmpeg.av_interleaved_write_frame(ofmtCtx, encPkt);
                                ffmpeg.av_packet_unref(encPkt);
                            }
                            ffmpeg.av_packet_free(&encPkt);
                            ffmpeg.av_frame_unref(filtFrame);
                        }
                    }
                    else
                    {
                        frame->pts = MonotonicVideoPts(frame->pts, ref lastVideoPts, vInStream->time_base, vEncCtx->time_base, vEncCtx->framerate);
                        ffmpeg.avcodec_send_frame(vEncCtx, frame);
                        AVPacket* encPkt = ffmpeg.av_packet_alloc();
                        while (ffmpeg.avcodec_receive_packet(vEncCtx, encPkt) == 0)
                        {
                            ffmpeg.av_packet_rescale_ts(encPkt, vEncCtx->time_base, vOutStream->time_base);
                            FixPacketDts(encPkt, ref lastVideoDts);
                            encPkt->stream_index = vOutStream->index;
                            ffmpeg.av_interleaved_write_frame(ofmtCtx, encPkt);
                            ffmpeg.av_packet_unref(encPkt);
                        }
                        ffmpeg.av_packet_free(&encPkt);
                    }
                    ffmpeg.av_frame_unref(frame);
                }
            }
            if (vFilterGraph != null)
            {
                ffmpeg.av_buffersrc_add_frame_flags(vFilterSrc, null, 0);
                while (ffmpeg.av_buffersink_get_frame(vFilterSink, filtFrame) == 0)
                {
                    filtFrame->pts = MonotonicVideoPts(filtFrame->pts, ref lastVideoPts, ffmpeg.av_buffersink_get_time_base(vFilterSink), vEncCtx->time_base, vEncCtx->framerate);
                    ffmpeg.avcodec_send_frame(vEncCtx, filtFrame);
                    AVPacket* encPkt = ffmpeg.av_packet_alloc();
                    while (ffmpeg.avcodec_receive_packet(vEncCtx, encPkt) == 0)
                    {
                        ffmpeg.av_packet_rescale_ts(encPkt, vEncCtx->time_base, vOutStream->time_base);
                        FixPacketDts(encPkt, ref lastVideoDts);
                        encPkt->stream_index = vOutStream->index;
                        ffmpeg.av_interleaved_write_frame(ofmtCtx, encPkt);
                        ffmpeg.av_packet_unref(encPkt);
                    }
                    ffmpeg.av_packet_free(&encPkt);
                    ffmpeg.av_frame_unref(filtFrame);
                }
            }
            if (hasVideo && vEncCtx != null)
            {
                ffmpeg.avcodec_send_frame(vEncCtx, null);
                while (true)
                {
                    AVPacket* encPkt = ffmpeg.av_packet_alloc();
                    ret = ffmpeg.avcodec_receive_packet(vEncCtx, encPkt);
                    if (ret == ffmpeg.AVERROR_EOF || ret == ffmpeg.AVERROR(ffmpeg.EAGAIN)) { ffmpeg.av_packet_free(&encPkt); break; }
                    if (ret < 0) { ffmpeg.av_packet_free(&encPkt); break; }
                    ffmpeg.av_packet_rescale_ts(encPkt, vEncCtx->time_base, vOutStream->time_base);
                    FixPacketDts(encPkt, ref lastVideoDts);
                    encPkt->stream_index = vOutStream->index;
                    ffmpeg.av_interleaved_write_frame(ofmtCtx, encPkt);
                    ffmpeg.av_packet_free(&encPkt);
                }
            }
            if (hasAudio && aEncCtx != null)
            {
                ffmpeg.avcodec_send_frame(aEncCtx, null);
                while (true)
                {
                    AVPacket* encPkt = ffmpeg.av_packet_alloc();
                    ret = ffmpeg.avcodec_receive_packet(aEncCtx, encPkt);
                    if (ret == ffmpeg.AVERROR_EOF || ret == ffmpeg.AVERROR(ffmpeg.EAGAIN)) { ffmpeg.av_packet_free(&encPkt); break; }
                    if (ret < 0) { ffmpeg.av_packet_free(&encPkt); break; }
                    ffmpeg.av_packet_rescale_ts(encPkt, aEncCtx->time_base, aOutStream->time_base);
                    FixPacketDts(encPkt, ref lastAudioDts);
                    encPkt->stream_index = aOutStream->index;
                    ffmpeg.av_interleaved_write_frame(ofmtCtx, encPkt);
                    ffmpeg.av_packet_free(&encPkt);
                }
            }
            ffmpeg.av_write_trailer(ofmtCtx);
        }
        finally
        {
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (filtFrame != null) ffmpeg.av_frame_free(&filtFrame);
            if (swrFrame != null) ffmpeg.av_frame_free(&swrFrame);
            if (vFilterGraph != null) ffmpeg.avfilter_graph_free(&vFilterGraph);
            if (swrCtx != null) ffmpeg.swr_free(&swrCtx);
            if (vDecCtx != null) ffmpeg.avcodec_free_context(&vDecCtx);
            if (aDecCtx != null) { ffmpeg.av_channel_layout_uninit(&aDecCtx->ch_layout); ffmpeg.avcodec_free_context(&aDecCtx); }
            if (vEncCtx != null) ffmpeg.avcodec_free_context(&vEncCtx);
            if (aEncCtx != null) { ffmpeg.av_channel_layout_uninit(&aEncCtx->ch_layout); ffmpeg.avcodec_free_context(&aEncCtx); }
            if (ifmtCtx != null) ffmpeg.avformat_close_input(&ifmtCtx);
            if (ofmtCtx != null)
            {
                if ((ofmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && ofmtCtx->pb != null)
                    ffmpeg.avio_closep(&ofmtCtx->pb);
                ffmpeg.avformat_free_context(ofmtCtx);
            }
            if (hwDeviceCtx != null) ffmpeg.av_buffer_unref(&hwDeviceCtx);
        }
    }

    private static unsafe long MonotonicVideoPts(long pts, ref long lastPts, AVRational srcTb, AVRational encTb, AVRational frameRate)
    {
        // Deinterlace filters (yadif/bwdif) rewrite frame pts (field-based ±1 offsets) and can produce
        // non-monotonic / NOPTS output; the muxer then rejects packets with EINVAL. Normalize here.
        if (pts != ffmpeg.AV_NOPTS_VALUE)
            pts = ffmpeg.av_rescale_q(pts, srcTb, encTb);
        long frameDur = 1;
        if (frameRate.num > 0 && frameRate.den > 0)
        {
            long d = ffmpeg.av_rescale_q_rnd(1, new AVRational { num = frameRate.den, den = frameRate.num }, encTb, AVRounding.AV_ROUND_UP);
            if (d > 0) frameDur = d;
        }
        if (pts == ffmpeg.AV_NOPTS_VALUE || (lastPts != ffmpeg.AV_NOPTS_VALUE && pts < lastPts))
            pts = lastPts == ffmpeg.AV_NOPTS_VALUE ? 0 : lastPts;
        if (pts < 0) pts = 0;
        lastPts = pts + frameDur;
        return pts;
    }

    private static unsafe void FixPacketDts(AVPacket* pkt, ref long lastDts)
    {
        if (pkt->dts == ffmpeg.AV_NOPTS_VALUE) pkt->dts = pkt->pts;
        if (lastDts != ffmpeg.AV_NOPTS_VALUE && pkt->dts <= lastDts)
        {
            pkt->dts = lastDts + 1;
            if (pkt->pts < pkt->dts) pkt->pts = pkt->dts;
        }
        lastDts = pkt->dts;
    }

    private unsafe int CreateVideoFilterGraph(AVCodecContext* decCtx, AVCodecContext* encCtx, string filterSpec, AVFilterGraph** outGraph, AVFilterContext** outSrc, AVFilterContext** outSink)
    {
        AVFilterGraph* graph = ffmpeg.avfilter_graph_alloc();
        if (graph == null) return ffmpeg.AVERROR(ffmpeg.ENOMEM);
        string pixFmtName = ffmpeg.av_get_pix_fmt_name(decCtx->pix_fmt) ?? "yuv420p";
        AVRational tb = decCtx->time_base;
        if (tb.num == 0 || tb.den == 0) tb = new AVRational { num = 1, den = 90000 };
        int w = decCtx->width;
        int h = decCtx->height;
        AVRational sar = decCtx->sample_aspect_ratio;
        string sarStr = sar.num == 0 ? "1/1" : $"{sar.num}/{sar.den}";
        string args = $"video_size={w}x{h}:pix_fmt={pixFmtName}:time_base={tb.num}/{tb.den}:pixel_aspect={sarStr}";
        AVFilter* bufferSrc = ffmpeg.avfilter_get_by_name("buffer");
        AVFilter* bufferSink = ffmpeg.avfilter_get_by_name("buffersink");
        if (bufferSrc == null || bufferSink == null) { ffmpeg.avfilter_graph_free(&graph); return ffmpeg.AVERROR(ffmpeg.EINVAL); }
        AVFilterContext* srcCtx = null;
        AVFilterContext* sinkCtx = null;
        int ret = ffmpeg.avfilter_graph_create_filter(&srcCtx, bufferSrc, "in", args, null, graph);
        if (ret < 0) { ffmpeg.avfilter_graph_free(&graph); return ret; }
        ret = ffmpeg.avfilter_graph_create_filter(&sinkCtx, bufferSink, "out", null, null, graph);
        if (ret < 0) { ffmpeg.avfilter_graph_free(&graph); return ret; }
        if (!string.IsNullOrWhiteSpace(filterSpec))
        {
            AVFilterInOut* outputs = ffmpeg.avfilter_inout_alloc();
            AVFilterInOut* inputs = ffmpeg.avfilter_inout_alloc();
            if (outputs == null || inputs == null) { ffmpeg.avfilter_graph_free(&graph); return ffmpeg.AVERROR(ffmpeg.ENOMEM); }
            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = srcCtx;
            outputs->pad_idx = 0;
            outputs->next = null;
            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = sinkCtx;
            inputs->pad_idx = 0;
            inputs->next = null;
            ret = ffmpeg.avfilter_graph_parse_ptr(graph, filterSpec, &inputs, &outputs, null);
            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);
            if (ret < 0) { ffmpeg.avfilter_graph_free(&graph); return ret; }
        }
        else
        {
            ret = ffmpeg.avfilter_link(srcCtx, 0, sinkCtx, 0);
            if (ret < 0) { ffmpeg.avfilter_graph_free(&graph); return ret; }
        }
        ret = ffmpeg.avfilter_graph_config(graph, null);
        if (ret < 0) { ffmpeg.avfilter_graph_free(&graph); return ret; }
        *outGraph = graph;
        *outSrc = srcCtx;
        *outSink = sinkCtx;
        return 0;
    }

    private static string BuildVideoFilterString(VideoTranscodeOptions o)
    {
        var filters = new List<string>();
        if (o.CropEnabled) filters.Add($"crop=iw-{o.CropLeft + o.CropRight}:ih-{o.CropTop + o.CropBottom}:{o.CropLeft}:{o.CropTop}");
        if (o.Deinterlace != DeinterlaceMode.None) filters.Add(o.Deinterlace == DeinterlaceMode.Bwdif ? "bwdif" : "yadif");
        if (o.Denoise != DenoiseMode.None) filters.Add(GetDenoiseFilter(o.Denoise));
        if (o.ScaleMode != ScaleMode.None) filters.Add(BuildScaleFilter(o));
        if (o.FpsMode != FpsMode.SameAsSource)
        {
            if (o.FpsMode == FpsMode.Fixed) filters.Add($"fps={o.FpsValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            else if (o.FpsMode == FpsMode.Peak) filters.Add($"fps={o.FpsValue.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        if (IsVaapiBackend(o.HardwareBackend)) filters.Add("format=nv12,hwupload");
        else if (IsQsvBackend(o.HardwareBackend)) filters.Add("format=nv12");
        return string.Join(",", filters);
    }

    private static string BuildGifFilterString(VideoTranscodeOptions o)
    {
        string fps = o.GifFps > 0 ? o.GifFps.ToString() : "15";
        string width = o.GifWidth > 0 ? o.GifWidth.ToString() : "480";
        var vfParts = new List<string>();
        vfParts.Add($"fps={fps}");
        vfParts.Add($"scale={width}:-1:flags=lanczos");
        if (o.CropEnabled) vfParts.Add($"crop=iw-{o.CropLeft + o.CropRight}:ih-{o.CropTop + o.CropBottom}:{o.CropLeft}:{o.CropTop}");
        if (o.Deinterlace != DeinterlaceMode.None) vfParts.Add(o.Deinterlace == DeinterlaceMode.Bwdif ? "bwdif" : "yadif");
        if (o.Denoise != DenoiseMode.None) vfParts.Add(GetDenoiseFilter(o.Denoise));
        string baseFilters = string.Join(",", vfParts);
        string dither = o.GifDither switch { GifDither.None => "none", GifDither.Bayer => "bayer:bayer_scale=5", GifDither.FloydSteinberg => "floyd_steinberg", GifDither.Sierpinski => "sierra2_4a", _ => "bayer:bayer_scale=5" };
        string stats = o.GifStatsMode == "single" ? "single" : "diff";
        return $"{baseFilters},split[s0][s1];[s0]palettegen=max_colors={o.GifMaxColors}:stats_mode={stats}[p];[s1][p]paletteuse=dither={dither}";
    }

    private static (int W, int H) CalculateOutputSize(int srcW, int srcH, VideoTranscodeOptions o)
    {
        if (o.Container == VideoContainer.Gif)
        {
            int gw = o.GifWidth > 0 ? o.GifWidth : 480;
            int gh = (int)Math.Round((double)srcH * gw / srcW);
            return (gw & ~1, gh & ~1);
        }
        return o.ScaleMode switch
        {
            ScaleMode.FitWithin => CalculateFitWithin(srcW, srcH, o.ScaleWidth, o.ScaleHeight),
            ScaleMode.Exact => (o.ScaleWidth & ~1, o.ScaleHeight & ~1),
            ScaleMode.Width => (o.ScaleWidth & ~1, (int)Math.Round((double)srcH * o.ScaleWidth / srcW) & ~1),
            ScaleMode.Height => ((int)Math.Round((double)srcW * o.ScaleHeight / srcH) & ~1, o.ScaleHeight & ~1),
            _ => (srcW & ~1, srcH & ~1)
        };
    }

    private static (int, int) CalculateFitWithin(int srcW, int srcH, int maxW, int maxH)
    {
        double rw = (double)maxW / srcW;
        double rh = (double)maxH / srcH;
        double r = Math.Min(rw, rh);
        if (r >= 1) return (srcW & ~1, srcH & ~1);
        return ((int)Math.Round(srcW * r) & ~1, (int)Math.Round(srcH * r) & ~1);
    }

    private static string BuildScaleFilter(VideoTranscodeOptions o) => o.ScaleMode switch
    {
        ScaleMode.FitWithin => $"scale=w={o.ScaleWidth}:h={o.ScaleHeight}:force_original_aspect_ratio=decrease:eval=frame:flags=lanczos",
        ScaleMode.Exact => $"scale={o.ScaleWidth}:{o.ScaleHeight}:flags=lanczos",
        ScaleMode.Width => $"scale={o.ScaleWidth}:-2:flags=lanczos",
        ScaleMode.Height => $"scale=-2:{o.ScaleHeight}:flags=lanczos",
        _ => $"scale={o.ScaleWidth}:{o.ScaleHeight}:flags=lanczos"
    };

    private static string GetDenoiseFilter(DenoiseMode m) => m switch
    {
        DenoiseMode.Hqdn3dLight => "hqdn3d=4:3:6:4.5",
        DenoiseMode.Hqdn3dMedium => "hqdn3d=8:6:8:6",
        DenoiseMode.Hqdn3dStrong => "hqdn3d=12:8:12:8",
        _ => "hqdn3d"
    };

    private static bool IsVaapiBackend(HardwareBackend hw) => (hw == HardwareBackend.Intel || hw == HardwareBackend.Amd) && !OperatingSystem.IsWindows();
    private static bool IsQsvBackend(HardwareBackend hw) => hw == HardwareBackend.Intel && OperatingSystem.IsWindows();
    private static bool IsAmfBackend(HardwareBackend hw) => hw == HardwareBackend.Amd && OperatingSystem.IsWindows();

    private static string MapHardwareEncoder(VideoCodec codec, HardwareBackend hw)
    {
        bool isVaapi = IsVaapiBackend(hw);
        return (codec, hw, isVaapi) switch
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
            _ => ""
        };
    }

    private static string MapVideoEncoderName(VideoTranscodeOptions o)
    {
        if (o.Container == VideoContainer.Gif) return "gif";
        if (o.HardwareBackend != HardwareBackend.Software)
        {
            string hw = MapHardwareEncoder(o.VideoCodec, o.HardwareBackend);
            if (!string.IsNullOrEmpty(hw)) return hw;
        }
        return MapSoftwareVideoEncoderName(o.VideoCodec);
    }

    private static string MapSoftwareVideoEncoderName(VideoCodec c) => c switch
    {
        VideoCodec.H264 => "libx264",
        VideoCodec.H265 => "libx265",
        VideoCodec.Av1Aom => "libaom-av1",
        VideoCodec.Av1Svt => "libsvtav1",
        VideoCodec.Vp8 => "libvpx",
        VideoCodec.Vp9 => "libvpx-vp9",
        VideoCodec.Gif => "gif",
        _ => "libx264"
    };

    private static AVCodecID MapVideoCodecId(VideoCodec c) => c switch
    {
        VideoCodec.H264 => AVCodecID.AV_CODEC_ID_H264,
        VideoCodec.H265 => AVCodecID.AV_CODEC_ID_HEVC,
        VideoCodec.Av1Aom => AVCodecID.AV_CODEC_ID_AV1,
        VideoCodec.Av1Svt => AVCodecID.AV_CODEC_ID_AV1,
        VideoCodec.Vp8 => AVCodecID.AV_CODEC_ID_VP8,
        VideoCodec.Vp9 => AVCodecID.AV_CODEC_ID_VP9,
        VideoCodec.Gif => AVCodecID.AV_CODEC_ID_GIF,
        _ => AVCodecID.AV_CODEC_ID_H264
    };

    private static string MapAudioEncoderName(AudioCodec c) => c switch
    {
        AudioCodec.Aac => "aac",
        AudioCodec.Mp3 => "libmp3lame",
        AudioCodec.Opus => "libopus",
        AudioCodec.Vorbis => "libvorbis",
        AudioCodec.Flac => "flac",
        AudioCodec.Ac3 => "ac3",
        _ => "aac"
    };

    private static (string? Key, string? Value, bool IsInt) MapPresetOption(VideoTranscodeOptions o)
    {
        PresetLevel p = o.Preset;
        HardwareBackend hw = o.HardwareBackend;
        VideoCodec codec = o.VideoCodec;
        if (o.Container == VideoContainer.Gif) return (null, null, false);
        if (hw == HardwareBackend.Software)
        {
            return codec switch
            {
                VideoCodec.H264 or VideoCodec.H265 => ("preset", MapPresetString(p), false),
                // SVT-AV1 preset is int 0-13 (lower = slower/better)
                VideoCodec.Av1Svt => ("preset", (13 - (int)p).ToString(), true),
                // libaom/libvpx use cpu-used int (higher = faster)
                VideoCodec.Av1Aom or VideoCodec.Vp8 or VideoCodec.Vp9 => ("cpu-used", Math.Clamp(8 - (int)p, 0, 8).ToString(), true),
                _ => (null, null, false)
            };
        }
        if (hw == HardwareBackend.Nvidia) return ("preset", MapNvencPreset(p), false);
        if (hw == HardwareBackend.Amd && !IsVaapiBackend(hw)) return ("preset", MapAmfPreset(p), false);
        return ("preset", MapQsvPreset(p), false);
    }

    private static string MapPresetString(PresetLevel p) => p switch
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

    private static string MapNvencPreset(PresetLevel p) => p switch
    {
        PresetLevel.Ultrafast => "p1",
        PresetLevel.Superfast => "p2",
        PresetLevel.Veryfast => "p3",
        PresetLevel.Faster => "p4",
        PresetLevel.Fast => "p4",
        PresetLevel.Medium => "p5",
        PresetLevel.Slow => "p6",
        PresetLevel.Slower => "p6",
        PresetLevel.Veryslow => "p7",
        PresetLevel.Placebo => "p7",
        _ => "p5"
    };

    private static string MapAmfPreset(PresetLevel p) => p switch
    {
        PresetLevel.Ultrafast => "speed",
        PresetLevel.Superfast => "speed",
        PresetLevel.Veryfast => "speed",
        PresetLevel.Faster => "balanced",
        PresetLevel.Fast => "balanced",
        PresetLevel.Medium => "balanced",
        PresetLevel.Slow => "quality",
        PresetLevel.Slower => "quality",
        PresetLevel.Veryslow => "quality",
        PresetLevel.Placebo => "quality",
        _ => "balanced"
    };

    private static string MapQsvPreset(PresetLevel p) => p switch
    {
        // QSV preset range is veryfast..veryslow (no ultrafast/placebo); clamp invalid values
        PresetLevel.Ultrafast => "veryfast",
        PresetLevel.Superfast => "veryfast",
        PresetLevel.Veryfast => "veryfast",
        PresetLevel.Faster => "faster",
        PresetLevel.Fast => "fast",
        PresetLevel.Medium => "medium",
        PresetLevel.Slow => "slow",
        PresetLevel.Slower => "slower",
        PresetLevel.Veryslow => "veryslow",
        PresetLevel.Placebo => "veryslow",
        _ => "medium"
    };

    private unsafe void SetCodecOption(AVCodecContext* ctx, AVDictionary** dict, string key, string value, bool isInt)
    {
        AVOption* opt = ffmpeg.av_opt_find(ctx, key, null, 0, ffmpeg.AV_OPT_SEARCH_CHILDREN);
        if (opt == null)
        {
            // Encoder does not expose this option — skip instead of failing avcodec_open2
            _logger.LogDebug("Encoder option {Key} not supported by {Codec}, skipped", key, Marshal.PtrToStringAnsi((IntPtr)ctx->codec->name));
            return;
        }
        if (isInt || opt->type == AVOptionType.AV_OPT_TYPE_INT || opt->type == AVOptionType.AV_OPT_TYPE_INT64 ||
            opt->type == AVOptionType.AV_OPT_TYPE_BOOL || opt->type == AVOptionType.AV_OPT_TYPE_FLAGS)
        {
            if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long lv))
                ffmpeg.av_opt_set_int(ctx, key, lv, 0);
        }
        else
        {
            ffmpeg.av_dict_set(dict, key, value, 0);
        }
    }

    private sealed class ScaledProgress : IProgress<double>
    {
        private readonly IProgress<double>? _inner;
        private readonly double _offset;
        private readonly double _scale;
        public ScaledProgress(IProgress<double>? inner, double offset, double scale) { _inner = inner; _offset = offset; _scale = scale; }
        public void Report(double value) => _inner?.Report(_offset + value * _scale);
    }

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

    private static string GetFormatForContainer(VideoContainer c) => c switch
    {
        VideoContainer.Mp4 => "mp4",
        VideoContainer.Mkv => "matroska",
        VideoContainer.WebM => "webm",
        VideoContainer.Mov => "mov",
        VideoContainer.Avi => "avi",
        VideoContainer.Gif => "gif",
        _ => "mp4"
    };
}
