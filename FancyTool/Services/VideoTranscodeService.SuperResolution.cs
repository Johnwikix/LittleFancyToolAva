using FancyToolAva.Models;
using FFmpeg.AutoGen;
using SkiaSharp;

namespace FancyToolAva.Services;

public sealed unsafe partial class VideoTranscodeService
{
    /// <summary>Runs preprocessing before inference, holding only one decoded and one enhanced frame.</summary>
    private sealed class VideoSrPipeline : IDisposable
    {
        private AVFilterGraph* _graph;
        private AVFilterContext* _source;
        private AVFilterContext* _sink;
        private AVFrame* _input = ffmpeg.av_frame_alloc();
        private AVFrame* _output = ffmpeg.av_frame_alloc();
        private SwsContext* _converter;
        private readonly byte*[] _sourceData = new byte*[8];
        private readonly int[] _sourceStride = new int[8];
        private readonly byte*[] _targetData = new byte*[4];
        private readonly int[] _targetStride = new int[4];
        private SKBitmap? _bitmap;
        private readonly IVideoFrameUpscaler _upscaler;
        private readonly int _scale;

        public AVFilterLink* OutputFormat => _sink->inputs[0];

        public VideoSrPipeline(AVCodecContext* decoder, AVStream* stream, VideoTranscodeOptions options, IVideoFrameUpscaler upscaler)
        {
            _upscaler = upscaler;
            _scale = options.SuperResolutionScale;
            try
            {
                _graph = ffmpeg.avfilter_graph_alloc();
                _source = CreateBufferSource(_graph, decoder, stream);
                _sink = CreateBufferSink(_graph);
                var pre = options.WithTrim(0, null);
                pre.Container = VideoContainer.Mp4;
                pre.HardwareBackend = HardwareBackend.Software;
                pre.ScaleMode = ScaleMode.None;
                if (options.Container == VideoContainer.Gif) { pre.FpsMode = FpsMode.Fixed; pre.FpsValue = options.GifFps; }
                AVBufferRef* unused = null;
                if (!BindGraphAndConfigure(_graph, _source, _sink, BuildFilterDescription(pre, "bgra"), false, "", &unused))
                    throw new InvalidOperationException("Super-resolution preprocessing failed");
                VideoFrameUpscaler.ValidateSize(OutputFormat->w, OutputFormat->h, _scale);
            }
            catch { Dispose(); throw; }
        }

        public void Send(AVFrame* frame)
        {
            int ret = ffmpeg.av_buffersrc_add_frame_flags(_source, frame, (int)AvBuffersrcFlag.AV_BUFFERSRC_FLAG_KEEP_REF);
            if (ret < 0) throw FFmpegNative.CreateException(ret, "Super-resolution frame input failed");
        }

        public bool Receive(CancellationToken ct, out AVFrame* frame)
        {
            ct.ThrowIfCancellationRequested();
            int ret = ffmpeg.av_buffersink_get_frame(_sink, _input);
            if (ret == ErrEagain || ret == ffmpeg.AVERROR_EOF) { frame = null; return false; }
            if (ret < 0) throw FFmpegNative.CreateException(ret, "Super-resolution preprocessing failed");
            try
            {
                if (_bitmap == null || _bitmap.Width != _input->width || _bitmap.Height != _input->height)
                {
                    _bitmap?.Dispose();
                    _bitmap = new SKBitmap(new SKImageInfo(_input->width, _input->height, SKColorType.Bgra8888, SKAlphaType.Opaque));
                }
                _converter = ffmpeg.sws_getCachedContext(_converter, _input->width, _input->height, (AVPixelFormat)_input->format,
                    _bitmap.Width, _bitmap.Height, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
                if (_converter == null) throw new OutOfMemoryException();
                for (uint i = 0; i < 8; i++) { _sourceData[i] = _input->data[i]; _sourceStride[i] = _input->linesize[i]; }
                _targetData[0] = (byte*)_bitmap.GetPixels();
                _targetStride[0] = _bitmap.RowBytes;
                ret = ffmpeg.sws_scale(_converter, _sourceData, _sourceStride, 0, _input->height, _targetData, _targetStride);
                if (ret < 0) throw FFmpegNative.CreateException(ret, "Super-resolution pixel conversion failed");
                using var enhanced = _upscaler.Upscale(_bitmap, _scale, ct);
                if (_output->width != enhanced.Width || _output->height != enhanced.Height)
                {
                    ffmpeg.av_frame_unref(_output);
                    _output->width = enhanced.Width;
                    _output->height = enhanced.Height;
                    _output->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                    ret = ffmpeg.av_frame_get_buffer(_output, 32);
                    if (ret < 0) throw FFmpegNative.CreateException(ret, "Super-resolution frame allocation failed");
                }
                ret = ffmpeg.av_frame_make_writable(_output);
                if (ret < 0) throw FFmpegNative.CreateException(ret, "Super-resolution frame allocation failed");
                int bytes = enhanced.Width * 4;
                for (int row = 0; row < enhanced.Height; row++)
                    Buffer.MemoryCopy((byte*)enhanced.GetPixels() + row * enhanced.RowBytes, _output->data[0] + row * _output->linesize[0], bytes, bytes);
                _output->pts = _input->pts;
                _output->duration = _input->duration;
                _output->time_base = OutputFormat->time_base;
                _output->sample_aspect_ratio = _input->sample_aspect_ratio;
                frame = _output;
                return true;
            }
            finally { ffmpeg.av_frame_unref(_input); }
        }

        public void Dispose()
        {
            _bitmap?.Dispose();
            _bitmap = null;
            var output = _output;
            var input = _input;
            ffmpeg.av_frame_free(&output);
            ffmpeg.av_frame_free(&input);
            _output = _input = null;
            if (_converter != null) ffmpeg.sws_freeContext(_converter);
            _converter = null;
            var graph = _graph;
            if (graph != null) ffmpeg.avfilter_graph_free(&graph);
            _graph = null;
        }
    }

    private static void DrainEnhancedFrames(VideoSrPipeline pipeline, CancellationToken ct,
        AVFilterContext* source, AVFilterContext* sink, AVFrame* filterFrame, AVCodecContext* encoder,
        AVFormatContext* output, AVStream* stream, AVPacket* packet, double total, IProgress<double>? progress)
    {
        while (pipeline.Receive(ct, out var frame))
            PushVideoFrame(source, sink, frame, filterFrame, encoder, output, stream, packet, total, progress);
    }
}
