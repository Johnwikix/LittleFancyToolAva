using FFmpeg.AutoGen;
using FancyToolAva.Models;

namespace FancyToolAva.Services;

public sealed unsafe partial class VideoTranscodeService
{
    private sealed class CudaDecoderFallbackException : Exception { }

    private static readonly AVCodecContext_get_format CudaDecoderFormat = static (context, formats) =>
    {
        for (var item = formats; *item != AVPixelFormat.AV_PIX_FMT_NONE; item++)
            if (*item == AVPixelFormat.AV_PIX_FMT_CUDA) return *item;
        return ffmpeg.avcodec_default_get_format(context, formats);
    };

    // FFmpeg 公共 hwcontext_cuda.h 的前两个字段；不访问其私有 internal 尾部。
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 16)]
    private struct CudaDeviceHeader { [System.Runtime.InteropServices.FieldOffset(8)] internal nint Stream; }

    private static AVBufferRef* CreateCudaDevice(CudaVideoContext cuda)
    {
        AVBufferRef* result = null;
        // AV_CUDA_USE_CURRENT_CONTEXT：借用已保留的主上下文。
        // FFmpeg 的 primary 模式要求 BLOCKING_SYNC，和 Windows ML 已激活的主上下文标志冲突。
        using var scope = cuda.Enter();
        int ret = ffmpeg.av_hwdevice_ctx_create(&result, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
            cuda.DeviceId.ToString(System.Globalization.CultureInfo.InvariantCulture), null, 2);
        if (ret < 0) throw FFmpegNative.CreateException(ret, "CUDA hardware device");
        var deviceContext = (AVHWDeviceContext*)result->data;
        ((CudaDeviceHeader*)deviceContext->hwctx)->Stream = cuda.Stream;
        return result;
    }

    private static bool CanDecodeCuda(AVStream* stream)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("FANCYTOOL_VIDEO_NVDEC") == "0") return false;
        // 8/10 位 4:2:0 尝试硬解；其他高位深或特殊色度继续使用软件路径。
        if ((AVPixelFormat)stream->codecpar->format is not (AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P
            or AVPixelFormat.AV_PIX_FMT_NV12 or AVPixelFormat.AV_PIX_FMT_YUV444P or AVPixelFormat.AV_PIX_FMT_YUVJ444P
            or AVPixelFormat.AV_PIX_FMT_YUV420P10LE or AVPixelFormat.AV_PIX_FMT_P010LE)) return false;
        var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null) return false;
        for (int i = 0; ; i++)
        {
            var config = ffmpeg.avcodec_get_hw_config(codec, i);
            if (config == null) return false;
            if (config->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA && (config->methods & 1) != 0) return true;
        }
    }

    private static AVPixelFormat CudaDownloadFormat(AVPixelFormat format) => format switch
    {
        AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P => AVPixelFormat.AV_PIX_FMT_NV12,
        AVPixelFormat.AV_PIX_FMT_YUVJ444P => AVPixelFormat.AV_PIX_FMT_YUV444P,
        AVPixelFormat.AV_PIX_FMT_YUV420P10LE => AVPixelFormat.AV_PIX_FMT_P010LE,
        _ => format
    };

    private static CudaVideoFrame CudaPixels(AVFrame* frame) => new((nint)frame->data[0], frame->width, frame->height, frame->linesize[0]);

    private static void SetCudaColor(AVFrame* frame, VideoColor color)
    {
        frame->color_primaries = color.Primaries;
        frame->color_trc = color.Transfer;
        frame->colorspace = color.Matrix;
        frame->color_range = color.Range;
    }

    /// <summary>FFmpeg 硬件帧引用拥有显存；输出队列与 NVENC 持有引用期间不会复用像素。</summary>
    private sealed class CudaFramePool : IDisposable
    {
        private AVBufferRef* _frames;
        private AVFrame* _frame;
        internal AVBufferRef* Frames => _frames;
        internal CudaFramePool(AVBufferRef* device, int width, int height, AVPixelFormat format = AVPixelFormat.AV_PIX_FMT_BGRA)
        {
            try
            {
                _frames = ffmpeg.av_hwframe_ctx_alloc(device);
                _frame = ffmpeg.av_frame_alloc();
                if (_frames == null || _frame == null) throw new OutOfMemoryException();
                var context = (AVHWFramesContext*)_frames->data;
                context->format = AVPixelFormat.AV_PIX_FMT_CUDA;
                context->sw_format = format;
                context->width = width;
                context->height = height;
                int ret = ffmpeg.av_hwframe_ctx_init(_frames);
                if (ret < 0) throw FFmpegNative.CreateException(ret, "CUDA frame pool");
            }
            catch { Dispose(); throw; }
        }
        internal AVFrame* Get()
        {
            using var gpuWork = VideoGpuWorkGate.Enter();
            ffmpeg.av_frame_unref(_frame);
            int ret = ffmpeg.av_hwframe_get_buffer(_frames, _frame, 0);
            if (ret < 0) throw FFmpegNative.CreateException(ret, "CUDA output frame");
            return _frame;
        }
        public void Dispose()
        {
            using var gpuWork = VideoGpuWorkGate.Enter();
            var frame = _frame; var frames = _frames;
            _frame = null;
            _frames = null;
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_buffer_unref(&frames);
        }
    }

    private sealed class CudaConvertStage(CudaVideoContext cuda, CudaFramePool pool, bool toNv12, VideoColor color, nint decoderStream = 0) : IVideoFrameStage
    {
        private nint _pending;
        public void Send(AVFrame* frame) => _pending = (nint)frame;
        public bool Receive(CancellationToken ct, out AVFrame* frame)
        {
            frame = null;
            if (_pending == 0) return false;
            ct.ThrowIfCancellationRequested();
            var input = (AVFrame*)_pending; _pending = 0;
            frame = pool.Get();
            long stamp = VideoPipelineMetrics.Start();
            if (toNv12) cuda.ConvertToNv12(CudaPixels(input), (nint)frame->data[0], (nint)frame->data[1], frame->linesize[0], frame->linesize[1], color.Matrix == AVColorSpace.AVCOL_SPC_BT709);
            else
            {
                if (input->width != frame->width || input->height != frame->height)
                    throw new NotSupportedException("CUDA pipeline requires a fixed frame size");
                if (input->format != (int)AVPixelFormat.AV_PIX_FMT_CUDA || input->hw_frames_ctx == null
                    || ((AVHWFramesContext*)input->hw_frames_ctx->data)->sw_format != AVPixelFormat.AV_PIX_FMT_NV12)
                    throw new NotSupportedException("CUDA inference requires NVDEC NV12 frames");
                cuda.WaitDecoder(decoderStream);
                cuda.ConvertNv12((nint)input->data[0], (nint)input->data[1], input->linesize[0], input->linesize[1], CudaPixels(frame),
                    color.Matrix == AVColorSpace.AVCOL_SPC_BT709, color.Range == AVColorRange.AVCOL_RANGE_JPEG);
            }
            var frameColor = toNv12 ? color : VideoColor.SdrRgb;
            frame->color_primaries = frameColor.Primaries;
            frame->color_trc = frameColor.Transfer;
            frame->colorspace = frameColor.Matrix;
            frame->color_range = frameColor.Range;
            VideoPipelineMetrics.Record(toNv12 ? VideoPipelineMetrics.Phase.CudaToNv12 : VideoPipelineMetrics.Phase.CudaToBgra, stamp);
            frame->pts = input->pts;
            frame->duration = input->duration;
            frame->sample_aspect_ratio = input->sample_aspect_ratio;
            return true;
        }
    }

    private sealed class CudaDecodeReadyStage(CudaVideoContext cuda, nint decoderStream) : IVideoFrameStage
    {
        private nint _pending;
        public void Send(AVFrame* frame) => _pending = (nint)frame;
        public bool Receive(CancellationToken ct, out AVFrame* frame)
        {
            frame = (AVFrame*)_pending; _pending = 0;
            if (frame == null) return false;
            ct.ThrowIfCancellationRequested(); cuda.WaitDecoder(decoderStream); return true;
        }
    }

    private sealed class CudaSrStage(IVideoCudaUpscaler model, CudaVideoContext cuda, CudaFramePool pool, int scale) : IVideoFrameStage, IDisposable
    {
        private nint _pending;
        private AVFrame* _lastInput;
        private AVFrame* _lastOutput;
        public void Send(AVFrame* frame) => _pending = (nint)frame;
        public bool Receive(CancellationToken ct, out AVFrame* frame)
        {
            frame = null;
            if (_pending == 0) return false;
            ct.ThrowIfCancellationRequested();
            var input = (AVFrame*)_pending;
            _pending = 0;
            long stamp = VideoPipelineMetrics.Start();
            bool same = _lastInput != null && cuda.HoldPair(CudaPixels(input), CudaPixels(_lastInput), false);
            VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrReuseCheck, stamp);
            if (same)
            {
                frame = _lastOutput;
                VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrFrameReuse, stamp);
            }
            else
            {
                frame = pool.Get();
                model.UpscaleCuda(CudaPixels(input), CudaPixels(frame), scale, ct);
                var nextInput = ffmpeg.av_frame_clone(input);
                var nextOutput = ffmpeg.av_frame_clone(frame);
                if (nextInput == null || nextOutput == null)
                {
                    ffmpeg.av_frame_free(&nextInput);
                    ffmpeg.av_frame_free(&nextOutput);
                    throw new OutOfMemoryException();
                }
                Dispose();
                _lastInput = nextInput;
                _lastOutput = nextOutput;
            }
            SetCudaColor(frame, VideoColor.SdrRgb);
            frame->pts = input->pts;
            frame->duration = input->duration;
            frame->sample_aspect_ratio = input->sample_aspect_ratio;
            return true;
        }
        public void Dispose()
        {
            var input = _lastInput;
            var output = _lastOutput;
            _lastInput = _lastOutput = null;
            ffmpeg.av_frame_free(&input);
            ffmpeg.av_frame_free(&output);
        }
    }

    /// <summary>保留原有 VFR/裁剪/尾帧时钟；两幅源帧及中间帧均为硬件帧引用。</summary>
    private sealed class CudaRifeStage : IVideoFrameStage, IDisposable
    {
        private readonly IVideoCudaInterpolator _model;
        private readonly CudaVideoContext _cuda;
        private readonly CudaFramePool _pool;
        private readonly AVRational _inputTimeBase;
        internal AVRational TimeBase { get; }
        private readonly bool _scenes;
        private readonly double _clipDuration;
        private AVFrame* _previous;
        private AVFrame* _current;
        private AVFrame* _output;
        private nint _pending;
        private bool _eof, _hold;
        private long _next;
        private double _previousTime, _currentTime, _lastDuration;

        internal CudaRifeStage(IVideoCudaInterpolator model, CudaVideoContext cuda, CudaFramePool pool, AVRational inputTimeBase,
            VideoTranscodeOptions options, double fps, long durationMs)
        {
            _model = model;
            _cuda = cuda;
            _pool = pool;
            _inputTimeBase = inputTimeBase;
            _scenes = options.InterpolationSceneDetection;
            var rate = ffmpeg.av_d2q(InterpolationRate(options, fps), 1_001_000);
            TimeBase = new AVRational { num = rate.den, den = rate.num };
            _clipDuration = Math.Min(options.TrimEndSeconds ?? double.PositiveInfinity,
                durationMs > 0 ? durationMs / 1000d : double.PositiveInfinity) - options.TrimStartSeconds;
            _lastDuration = 1 / (fps > 0 ? fps : 30);
            _output = ffmpeg.av_frame_alloc();
            if (_output == null) throw new OutOfMemoryException();
        }
        public void Send(AVFrame* frame) { if (frame == null) _eof = true; else _pending = (nint)frame; }
        public bool Receive(CancellationToken ct, out AVFrame* frame)
        {
            frame = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                double time = _next * ffmpeg.av_q2d(TimeBase);
                if (time >= _clipDuration - .0000001) return false;
                if (_current != null)
                {
                    if (time < _currentTime - .0000001)
                    {
                        var pixels = _previous;
                        if (!_hold && time > _previousTime + .0000001)
                        {
                            pixels = _pool.Get();
                            _model.InterpolateCuda(Math.Clamp((time - _previousTime) / (_currentTime - _previousTime), .00000001, .99999999), CudaPixels(pixels), ct);
                        }
                        frame = Output(pixels); return true;
                    }
                    var old = _previous;
                    ffmpeg.av_frame_free(&old);
                    _previous = _current;
                    _current = null;
                    _previousTime = _currentTime;
                }
                if (_pending != 0)
                {
                    var input = (AVFrame*)_pending; _pending = 0;
                    double position = input->pts * ffmpeg.av_q2d(_inputTimeBase);
                    if (_previous != null && position <= _previousTime + .0000001) continue;
                    if (input->duration > 0) _lastDuration = input->duration * ffmpeg.av_q2d(_inputTimeBase);
                    else if (_previous != null) _lastDuration = position - _previousTime;
                    var copy = ffmpeg.av_frame_clone(input);
                    if (copy == null) throw new OutOfMemoryException();
                    if (_previous == null) { _previous = copy; _previousTime = position; continue; }
                    _current = copy; _currentTime = position;
                    _hold = _cuda.HoldPair(CudaPixels(_previous), CudaPixels(_current), _scenes);
                    if (!_hold) _model.SetCudaFrames(CudaPixels(_previous), CudaPixels(_current), ct);
                    continue;
                }
                if (!_eof || _previous == null) return false;
                double end = double.IsFinite(_clipDuration) ? _clipDuration : _previousTime + _lastDuration;
                if (time >= end - .0000001) return false;
                frame = Output(_previous); return true;
            }
        }
        private AVFrame* Output(AVFrame* pixels)
        {
            ffmpeg.av_frame_unref(_output);
            int ret = ffmpeg.av_frame_ref(_output, pixels);
            if (ret < 0) throw FFmpegNative.CreateException(ret, "CUDA RIFE frame reference");
            _output->pts = _next++;
            _output->duration = 1;
            _output->time_base = TimeBase;
            _output->sample_aspect_ratio = _previous->sample_aspect_ratio;
            SetCudaColor(_output, VideoColor.SdrRgb);
            return _output;
        }
        public void Dispose()
        {
            var a = _previous; var b = _current; var c = _output;
            _previous = _current = _output = null;
            ffmpeg.av_frame_free(&a);
            ffmpeg.av_frame_free(&b);
            ffmpeg.av_frame_free(&c);
        }
    }
    private sealed class CudaVideoPlan : IDisposable
    {
        private readonly List<IDisposable> _owned = new();
        internal IVideoFrameStage[] Stages { get; private set; } = [];
        internal CudaFramePool EncoderPool { get; private set; } = null!;
        internal AVRational TimeBase { get; private set; }
        internal int Width { get; private set; }
        internal int Height { get; private set; }
        private T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }

        internal static CudaVideoPlan? TryCreate(AVCodecContext* decoder, AVStream* stream, VideoTranscodeOptions options,
            double fps, long durationMs, IVideoFrameUpscaler? upscaler, IVideoInterpolationService? service, CancellationToken ct)
        {
            if (Environment.GetEnvironmentVariable("FANCYTOOL_VIDEO_CUDA") == "0" || decoder->hw_device_ctx == null
                || options.HardwareBackend != HardwareBackend.Nvidia || options.Container == VideoContainer.Gif
                || options.InterpolationMode is not (VideoInterpolationMode.None or VideoInterpolationMode.Rife)
                || options.CropEnabled || options.Denoise != DenoiseMode.None || options.Deinterlace != DeinterlaceMode.None
                || options.ScaleMode != ScaleMode.None || options.FpsMode != FpsMode.SameAsSource
                || decoder->chroma_sample_location is not (AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED or AVChromaLocation.AVCHROMA_LOC_LEFT)
                || ((decoder->width | decoder->height) & 1) != 0
                || (AVPixelFormat)stream->codecpar->format is not (AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_NV12
                    or AVPixelFormat.AV_PIX_FMT_YUV420P10LE or AVPixelFormat.AV_PIX_FMT_P010LE)
                || (upscaler != null && upscaler is not IVideoCudaUpscaler { SupportsCuda: true })) return null;
            if (upscaler != null) VideoFrameUpscaler.ValidateSize(decoder->width, decoder->height, options.SuperResolutionScale);
            var color = ReadColor(decoder);
            nint decoderStream = ((CudaDeviceHeader*)((AVHWDeviceContext*)decoder->hw_device_ctx->data)->hwctx)->Stream;
            var outputColor = ResolveOutputColor(options, color);
            if (color.IsHdr)
            {
                if (color.Primaries != AVColorPrimaries.AVCOL_PRI_BT2020 || color.Matrix != AVColorSpace.AVCOL_SPC_BT2020_NCL) return null;
            }
            else if (color.Matrix is not (AVColorSpace.AVCOL_SPC_BT709 or AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M)
                || color.Primaries != AVColorPrimaries.AVCOL_PRI_BT709 || color.Transfer != AVColorTransferCharacteristic.AVCOL_TRC_BT709
                || (AVPixelFormat)stream->codecpar->format is AVPixelFormat.AV_PIX_FMT_YUV420P10LE or AVPixelFormat.AV_PIX_FMT_P010LE) return null;
            var plan = new CudaVideoPlan();
            try
            {
                int width = decoder->width, height = decoder->height;
                int deviceId = upscaler?.Device.TensorRtDeviceId ?? 0;
                if (deviceId != 0) { plan.Dispose(); return null; }
                bool srFirst = UpscaleBeforeInterpolation(options, decoder);
                IVideoFrameInterpolator? rife = null;
                if (options.InterpolationMode == VideoInterpolationMode.Rife)
                {
                    int scale = srFirst ? options.SuperResolutionScale : 1;
                    rife = plan.Own((service ?? throw new InvalidOperationException("RIFE service unavailable"))
                        .CreateSessionAsync(options, width * scale, height * scale, ct).GetAwaiter().GetResult());
                    if (rife is not IVideoCudaInterpolator { SupportsCuda: true } || rife.Device.TensorRtDeviceId != deviceId)
                    { plan.Dispose(); return null; }
                }
                var cuda = plan.Own(new CudaVideoContext(deviceId));
                var stages = new List<IVideoFrameStage>();
                var timeBase = stream->time_base;
                if (rife != null || upscaler != null)
                {
                    var rgb = plan.Own(new CudaFramePool(decoder->hw_device_ctx, width, height));
                    if (color.IsHdr) stages.Add(plan.Own(new CudaHdrStage(cuda, rgb, color, VideoColor.SdrRgb, true, decoderStream)));
                    else stages.Add(new CudaConvertStage(cuda, rgb, false, color, decoderStream));
                    void AddSr()
                    {
                        width *= options.SuperResolutionScale; height *= options.SuperResolutionScale;
                        var pool = plan.Own(new CudaFramePool(decoder->hw_device_ctx, width, height));
                        stages.Add(plan.Own(new CudaSrStage((IVideoCudaUpscaler)upscaler!, cuda, pool, options.SuperResolutionScale)));
                    }
                    void AddRife()
                    {
                        var pool = plan.Own(new CudaFramePool(decoder->hw_device_ctx, width, height));
                        var stage = plan.Own(new CudaRifeStage((IVideoCudaInterpolator)rife!, cuda, pool, timeBase, options, fps, durationMs));
                        stages.Add(stage); timeBase = stage.TimeBase;
                    }
                    if (srFirst) AddSr();
                    if (rife != null) AddRife();
                    if (upscaler != null && !srFirst) AddSr();
                }
                bool neural = rife != null || upscaler != null;
                plan.EncoderPool = plan.Own(new CudaFramePool(decoder->hw_device_ctx, width, height,
                    outputColor.IsHdr ? AVPixelFormat.AV_PIX_FMT_P010LE : AVPixelFormat.AV_PIX_FMT_NV12));
                if (neural)
                {
                    if (outputColor.IsHdr) stages.Add(plan.Own(new CudaHdrStage(cuda, plan.EncoderPool, VideoColor.SdrRgb, outputColor, false)));
                    else stages.Add(new CudaConvertStage(cuda, plan.EncoderPool, true, outputColor));
                }
                else if (color == outputColor) stages.Add(new CudaDecodeReadyStage(cuda, decoderStream));
                else stages.Add(plan.Own(new CudaHdrStage(cuda, plan.EncoderPool, color, outputColor, false, decoderStream)));
                plan.Stages = stages.ToArray();
                plan.TimeBase = timeBase;
                plan.Width = width;
                plan.Height = height;
                return plan;
            }
            catch { try { plan.Dispose(); } catch { } throw; }
        }
        public void Dispose()
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
            for (int i = _owned.Count - 1; i >= 0; i--)
                try { _owned[i].Dispose(); } catch (Exception error) { failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
            _owned.Clear();
            failure?.Throw();
        }
    }

}
