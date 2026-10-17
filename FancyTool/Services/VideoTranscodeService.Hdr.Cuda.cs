using FFmpeg.AutoGen;

namespace FancyToolAva.Services;

public sealed unsafe partial class VideoTranscodeService
{
    /// <summary>仅在色彩/静态亮度变化时烘焙 LUT；像素处理时只在 GPU 查表。</summary>
    private sealed class CudaColorLut(CudaVideoContext cuda, VideoColor source, VideoColor target) : IDisposable
    {
        internal const int Size = 65;
        private readonly record struct Key(VideoColor Source, VideoColor Target, double Peak);
        private static readonly Dictionary<Key, float[]> Cache = new();
        private static readonly Queue<Key> Order = new();
        private static readonly object Gate = new();
        private CudaVideoContext.CudaBuffer? _buffer;
        private double _peak = double.NaN;

        internal nint Get(AVFrame* metadata)
        {
            double peak = SignalPeak(metadata, source);
            if (_buffer != null && peak == _peak) return _buffer.Frame.Data;
            long stamp = VideoPipelineMetrics.Start();
            var key = new Key(Rgb(source), Rgb(target), peak);
            float[] values;
            lock (Gate)
            {
                if (!Cache.TryGetValue(key, out values!))
                {
                    values = Bake(key.Source, key.Target, metadata);
                    if (Cache.Count == 8) Cache.Remove(Order.Dequeue());
                    Cache.Add(key, values);
                    Order.Enqueue(key);
                }
            }
            var next = cuda.UploadLut(values);
            var old = _buffer;
            _buffer = next;
            _peak = peak;
            old?.Dispose();
            VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.HdrLut, stamp);
            return next.Frame.Data;
        }

        private static VideoColor Rgb(VideoColor color) => color with
        { Matrix = AVColorSpace.AVCOL_SPC_RGB, Range = AVColorRange.AVCOL_RANGE_JPEG };

        private static double SignalPeak(AVFrame* frame, VideoColor color)
        {
            if (!color.IsHdr) return 0;
            var light = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
            if (light != null && ((AVContentLightMetadata*)light->data)->MaxCLL > 0)
                return ((AVContentLightMetadata*)light->data)->MaxCLL;
            var master = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
            if (master != null && ((AVMasteringDisplayMetadata*)master->data)->has_luminance != 0)
            {
                double value = ffmpeg.av_q2d(((AVMasteringDisplayMetadata*)master->data)->max_luminance);
                if (value > 0 && double.IsFinite(value)) return value;
            }
            return color.Transfer == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 ? 10000 : 1000;
        }

        private static float[] Bake(VideoColor source, VideoColor target, AVFrame* metadata)
        {
            AVFilterGraph* graph = ffmpeg.avfilter_graph_alloc();
            AVFrame* input = ffmpeg.av_frame_alloc();
            AVFrame* output = ffmpeg.av_frame_alloc();
            try
            {
                if (graph == null || input == null || output == null) throw new OutOfMemoryException();
                // r 变化最快，与 CUDA 四面体插值及现有 .cube 格式的顺序一致。
                int width = Size * Size, height = Size;
                input->width = width;
                input->height = height;
                input->format = (int)AVPixelFormat.AV_PIX_FMT_GBRPF32LE;
                int result = ffmpeg.av_frame_get_buffer(input, 32);
                if (result >= 0) result = ffmpeg.av_frame_copy_props(input, metadata);
                if (result < 0) throw FFmpegNative.CreateException(result, "HDR LUT input");
                input->pts = 0;
                input->color_primaries = source.Primaries;
                input->color_trc = source.Transfer;
                input->colorspace = source.Matrix;
                input->color_range = source.Range;
                for (int b = 0; b < Size; b++)
                    for (int g = 0; g < Size; g++)
                        for (int r = 0; r < Size; r++)
                        {
                            int x = g * Size + r;
                            ((float*)(input->data[0] + b * input->linesize[0]))[x] = g / (float)(Size - 1);
                            ((float*)(input->data[1] + b * input->linesize[1]))[x] = b / (float)(Size - 1);
                            ((float*)(input->data[2] + b * input->linesize[2]))[x] = r / (float)(Size - 1);
                        }
                var decoder = new AVCodecContext { width = width, height = height, pix_fmt = AVPixelFormat.AV_PIX_FMT_GBRPF32LE,
                    sample_aspect_ratio = new AVRational { num = 1, den = 1 } };
                var stream = new AVStream { time_base = new AVRational { num = 1, den = 1 } };
                var src = CreateBufferSource(graph, &decoder, &stream);
                var sink = CreateBufferSink(graph);
                AVBufferRef* unused = null;
                if (!BindGraphAndConfigure(graph, src, sink, BuildColorFilters(source, target) + ",format=gbrpf32le", false, "", &unused))
                    throw new InvalidOperationException("HDR LUT color graph");
                result = ffmpeg.av_buffersrc_add_frame_flags(src, input, (int)AvBuffersrcFlag.AV_BUFFERSRC_FLAG_KEEP_REF);
                if (result >= 0) result = ffmpeg.av_buffersink_get_frame(sink, output);
                if (result < 0) throw FFmpegNative.CreateException(result, "HDR LUT output");
                var values = new float[Size * Size * Size * 4];
                for (int b = 0; b < Size; b++)
                    for (int x = 0; x < width; x++)
                    {
                        int index = (b * width + x) * 4;
                        values[index] = ((float*)(output->data[2] + b * output->linesize[2]))[x];
                        values[index + 1] = ((float*)(output->data[0] + b * output->linesize[0]))[x];
                        values[index + 2] = ((float*)(output->data[1] + b * output->linesize[1]))[x];
                        if (!float.IsFinite(values[index]) || !float.IsFinite(values[index + 1]) || !float.IsFinite(values[index + 2]))
                            throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_HdrCudaInvalidLut"));
                    }
                return values;
            }
            finally
            {
                ffmpeg.av_frame_free(&input);
                ffmpeg.av_frame_free(&output);
                ffmpeg.avfilter_graph_free(&graph);
            }
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = null;
            buffer?.Dispose();
        }
    }

    private static int CudaMatrix(VideoColor color) => color.Matrix == AVColorSpace.AVCOL_SPC_BT2020_NCL ? 2
        : color.Matrix == AVColorSpace.AVCOL_SPC_BT709 ? 1 : 0;

    private static int CudaPixelKind(AVFrame* frame)
    {
        if (frame->hw_frames_ctx == null) throw new InvalidOperationException("Missing CUDA frame pool");
        return ((AVHWFramesContext*)frame->hw_frames_ctx->data)->sw_format switch
        {
            AVPixelFormat.AV_PIX_FMT_BGRA => 0,
            AVPixelFormat.AV_PIX_FMT_NV12 => 1,
            AVPixelFormat.AV_PIX_FMT_P010LE => 2,
            _ => throw new NotSupportedException("CUDA HDR frame format")
        };
    }

    private sealed class CudaHdrStage : IVideoFrameStage, IDisposable
    {
        private readonly CudaVideoContext _cuda;
        private readonly CudaFramePool _pool;
        private readonly VideoColor _source, _target;
        private readonly CudaColorLut _lut;
        private readonly bool _bgra;
        private readonly nint _decoderStream;
        private nint _pending;

        internal CudaHdrStage(CudaVideoContext cuda, CudaFramePool pool, VideoColor source, VideoColor target, bool bgra, nint decoderStream = 0)
        {
            _cuda = cuda;
            _pool = pool;
            _source = source;
            _target = target;
            _bgra = bgra;
            _decoderStream = decoderStream;
            _lut = new(cuda, source, target);
        }

        public void Send(AVFrame* frame) => _pending = (nint)frame;
        public bool Receive(CancellationToken ct, out AVFrame* frame)
        {
            frame = null;
            if (_pending == 0) return false;
            ct.ThrowIfCancellationRequested();
            var input = (AVFrame*)_pending;
            _pending = 0;
            frame = _pool.Get();
            if (input->width != frame->width || input->height != frame->height)
                throw new NotSupportedException(LocalizationRegistry.Get("VideoTranscode.Msg_HdrCudaFormatChanged"));
            if (_decoderStream != 0) _cuda.WaitDecoder(_decoderStream);
            if (ReadColor(input) != _source) throw new NotSupportedException(LocalizationRegistry.Get("VideoTranscode.Msg_HdrCudaFormatChanged"));
            var lut = _lut.Get(input);
            ct.ThrowIfCancellationRequested();
            long stamp = VideoPipelineMetrics.Start();
            int kind = CudaPixelKind(input);
            if (_bgra)
                _cuda.HdrBgra((nint)input->data[0], (nint)input->data[1], input->linesize[0], input->linesize[1],
                    kind, CudaMatrix(_source), _source.Range == AVColorRange.AVCOL_RANGE_JPEG, lut, CudaColorLut.Size, CudaPixels(frame));
            else
                _cuda.HdrYuv((nint)input->data[0], (nint)input->data[1], input->linesize[0], input->linesize[1],
                    input->width, input->height, kind, CudaMatrix(_source), _source.Range == AVColorRange.AVCOL_RANGE_JPEG, lut, CudaColorLut.Size,
                    (nint)frame->data[0], (nint)frame->data[1], frame->linesize[0], frame->linesize[1], _target.IsHdr, CudaMatrix(_target));
            VideoPipelineMetrics.Record(_bgra ? VideoPipelineMetrics.Phase.CudaHdrToneMap
                : _target.IsHdr ? VideoPipelineMetrics.Phase.CudaToP010 : VideoPipelineMetrics.Phase.CudaToNv12, stamp);
            frame->pts = input->pts;
            frame->duration = input->duration;
            frame->sample_aspect_ratio = input->sample_aspect_ratio;
            frame->color_primaries = _target.Primaries;
            frame->color_trc = _target.Transfer;
            frame->colorspace = _target.Matrix;
            frame->color_range = _target.Range;
            return true;
        }

        public void Dispose() => _lut.Dispose();
    }
}
