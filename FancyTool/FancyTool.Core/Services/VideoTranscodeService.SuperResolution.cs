using FancyToolAva.Models;
using FFmpeg.AutoGen;
using SkiaSharp;

namespace FancyToolAva.Services;

public sealed unsafe partial class VideoTranscodeService
{
    /// <summary>Runs preprocessing and bounded single-frame or temporal-window inference.</summary>
    private sealed class VideoSrPipeline : IDisposable, IVideoFrameStage
    {
        private AVFilterGraph* _graph;
        private AVFilterContext* _source;
        private AVFilterContext* _sink;
        private AVFrame* _input = ffmpeg.av_frame_alloc();
        private AVFrame* _output;
        private AVFrame* _reuseInput;
        private AVFrame* _reuseOutput;
        private readonly bool _reuseFrames;
        private readonly BgraFramePool _outputPool = new();
        private SwsContext* _converter;
        private readonly byte*[] _sourceData = new byte*[8];
        private readonly int[] _sourceStride = new int[8];
        private readonly byte*[] _targetData = new byte*[4];
        private readonly int[] _targetStride = new int[4];
        private SKBitmap? _bitmap;
        private SKBitmap? _destinationBitmap;
        private bool _bitmapOwnsPixels;
        private readonly IVideoFrameUpscaler _upscaler;
        private readonly int _scale;
        private readonly List<SKBitmap> _pending = [];
        private readonly List<(long Pts, long Duration, AVRational Aspect)> _timestamps = [];
        private readonly Queue<(SKBitmap Bitmap, long Pts, long Duration, AVRational Aspect)> _ready = new();

        public AVFilterLink* OutputFormat => _sink->inputs[0];

        public VideoSrPipeline(AVCodecContext* decoder, AVStream* stream, VideoTranscodeOptions options, IVideoFrameUpscaler upscaler, bool preprocessed = false)
        {
            _upscaler = upscaler;
            _scale = options.SuperResolutionScale;
            _reuseFrames = upscaler is IVideoFrameReuseUpscaler;
            try
            {
                if (_reuseFrames)
                {
                    _reuseInput = ffmpeg.av_frame_alloc();
                    _reuseOutput = ffmpeg.av_frame_alloc();
                    if (_reuseInput == null || _reuseOutput == null) throw new OutOfMemoryException();
                }
                _graph = ffmpeg.avfilter_graph_alloc();
                if (_graph == null || _input == null) throw new OutOfMemoryException();
                _source = CreateBufferSource(_graph, decoder, stream);
                _sink = CreateBufferSink(_graph);
                var pre = options.WithTrim(0, null);
                pre.Container = VideoContainer.Mp4;
                pre.HardwareBackend = HardwareBackend.Software;
                pre.ScaleMode = ScaleMode.None;
                if (options.InterpolationMode != VideoInterpolationMode.None)
                {
                    pre.FpsMode = FpsMode.SameAsSource;
                    if (preprocessed) { pre.CropEnabled = false; pre.Deinterlace = DeinterlaceMode.None; pre.Denoise = DenoiseMode.None; }
                }
                else if (options.Container == VideoContainer.Gif) { pre.FpsMode = FpsMode.Fixed; pre.FpsValue = options.GifFps; }
                AVBufferRef* unused = null;
                if (!BindGraphAndConfigure(_graph, _source, _sink,
                    BuildFilterDescription(pre, "bgra", BuildColorFilters(ReadColor(decoder), VideoColor.SdrRgb)), false, "", &unused))
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
            if (_upscaler is IVideoFrameSequenceUpscaler sequence) return ReceiveTemporal(sequence, ct, out frame);
            int ret = ffmpeg.av_buffersink_get_frame(_sink, _input);
            if (ret == ErrEagain || ret == ffmpeg.AVERROR_EOF) { frame = null; return false; }
            if (ret < 0) throw FFmpegNative.CreateException(ret, "Super-resolution preprocessing failed");
            try
            {
                if (TryReuseOutput(ct))
                {
                    WriteTimestamp(_input->pts, _input->duration, _input->sample_aspect_ratio);
                    frame = _output;
                    return true;
                }
                var source = ConvertInput();
                bool written = false;
                if (_upscaler is IVideoFrameDestinationUpscaler writer)
                {
                    _output = _outputPool.Get(source.Width * _scale, source.Height * _scale);
                    _destinationBitmap ??= new SKBitmap();
                    WrapBgraFrame(_destinationBitmap, _output);
                    written = writer.TryUpscaleInto(source, _scale, _destinationBitmap, ct);
                }
                if (written) WriteTimestamp(_input->pts, _input->duration, _input->sample_aspect_ratio);
                else
                {
                    using var enhanced = _upscaler.Upscale(source, _scale, ct);
                    WriteOutput(enhanced, _input->pts, _input->duration, _input->sample_aspect_ratio);
                }
                CacheOutput();
                frame = _output;
                return true;
            }
            finally { ffmpeg.av_frame_unref(_input); }
        }

        private bool TryReuseOutput(CancellationToken ct)
        {
            if (!_reuseFrames) return false;
            long stamp = VideoPipelineMetrics.Start();
            try
            {
                int bytes = _input->width * 4;
                if (_reuseInput->data[0] == null || _reuseOutput->data[0] == null
                    || _input->format != (int)AVPixelFormat.AV_PIX_FMT_BGRA
                    || _input->width != _reuseInput->width || _input->height != _reuseInput->height
                    || _input->linesize[0] < bytes || _reuseInput->linesize[0] < bytes) return false;
                int height = _input->height;
                var current = new ReadOnlySpan<byte>(_input->data[0], checked((height - 1) * _input->linesize[0] + bytes));
                var previous = new ReadOnlySpan<byte>(_reuseInput->data[0], checked((height - 1) * _reuseInput->linesize[0] + bytes));
                if (!VideoBgraPixels.EqualRows(current, _input->linesize[0], previous, _reuseInput->linesize[0], _input->width, height, ct)) return false;
                // 只修改自有 AVFrame 的时间信息；已发出的克隆保留各自的 PTS 和像素引用。
                _output = _reuseOutput;
                VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrFrameReuse, stamp);
                return true;
            }
            finally { VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrReuseCheck, stamp); }
        }

        private void CacheOutput()
        {
            if (!_reuseFrames) return;
            ffmpeg.av_frame_unref(_reuseInput);
            ffmpeg.av_frame_unref(_reuseOutput);
            if (_input->format != (int)AVPixelFormat.AV_PIX_FMT_BGRA || _input->linesize[0] < _input->width * 4) return;
            int result = ffmpeg.av_frame_ref(_reuseInput, _input);
            if (result >= 0) result = ffmpeg.av_frame_ref(_reuseOutput, _output);
            if (result < 0) throw FFmpegNative.CreateException(result,
                LocalizationRegistry.Get("VideoTranscode.Msg_PipelineFailed", "av_frame_ref/reuse"));
        }

        private SKBitmap ConvertInput()
        {
            if (_input->format == (int)AVPixelFormat.AV_PIX_FMT_BGRA && _input->linesize[0] >= _input->width * 4)
            {
                _bitmap ??= new SKBitmap();
                WrapBgraFrame(_bitmap, _input);
                _bitmapOwnsPixels = false;
                return _bitmap;
            }
            // 负步长或非 BGRA 输入仍由 swscale 转换到自有缓冲。
            if (_bitmap == null || !_bitmapOwnsPixels || _bitmap.Width != _input->width || _bitmap.Height != _input->height)
            {
                _bitmap?.Dispose();
                _bitmap = new SKBitmap(new SKImageInfo(_input->width, _input->height, SKColorType.Bgra8888, SKAlphaType.Opaque));
                _bitmapOwnsPixels = true;
            }
            _converter = ffmpeg.sws_getCachedContext(_converter, _input->width, _input->height, (AVPixelFormat)_input->format,
                _bitmap.Width, _bitmap.Height, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_converter == null) throw new OutOfMemoryException();
            for (uint i = 0; i < 8; i++) { _sourceData[i] = _input->data[i]; _sourceStride[i] = _input->linesize[i]; }
            _targetData[0] = (byte*)_bitmap.GetPixels();
            _targetStride[0] = _bitmap.RowBytes;
            int ret = ffmpeg.sws_scale(_converter, _sourceData, _sourceStride, 0, _input->height, _targetData, _targetStride);
            if (ret < 0) throw FFmpegNative.CreateException(ret, "Super-resolution pixel conversion failed");
            return _bitmap;
        }

        private void WriteOutput(SKBitmap enhanced, long pts, long duration, AVRational aspect)
        {
            _output = _outputPool.Get(enhanced.Width, enhanced.Height);
            int bytes = enhanced.Width * 4;
            for (int row = 0; row < enhanced.Height; row++)
                Buffer.MemoryCopy((byte*)enhanced.GetPixels() + row * enhanced.RowBytes,
                    _output->data[0] + row * _output->linesize[0], bytes, bytes);
            WriteTimestamp(pts, duration, aspect);
        }

        private void WriteTimestamp(long pts, long duration, AVRational aspect)
        {
            _output->pts = pts;
            _output->duration = duration;
            _output->time_base = OutputFormat->time_base;
            _output->sample_aspect_ratio = aspect;
        }

        private bool ReceiveTemporal(IVideoFrameSequenceUpscaler sequence, CancellationToken ct, out AVFrame* frame)
        {
            frame = null;
            if (_ready.Count == 0)
            {
                while (_pending.Count < sequence.TemporalWindow)
                {
                    ct.ThrowIfCancellationRequested();
                    int ret = ffmpeg.av_buffersink_get_frame(_sink, _input);
                    if (ret == ErrEagain) return false;
                    if (ret == ffmpeg.AVERROR_EOF) break;
                    if (ret < 0) throw FFmpegNative.CreateException(ret, "Super-resolution preprocessing failed");
                    try
                    {
                        _pending.Add(ConvertInput().Copy() ?? throw new OutOfMemoryException());
                        _timestamps.Add((_input->pts, _input->duration, _input->sample_aspect_ratio));
                    }
                    finally { ffmpeg.av_frame_unref(_input); }
                }
                if (_pending.Count == 0) return false;
                try
                {
                    var enhanced = sequence.UpscaleSequence(_pending, _scale, ct);
                    for (int i = 0; i < enhanced.Length; i++)
                    {
                        var timestamp = _timestamps[i];
                        _ready.Enqueue((enhanced[i], timestamp.Pts, timestamp.Duration, timestamp.Aspect));
                    }
                }
                finally
                {
                    foreach (var pending in _pending) pending.Dispose();
                    _pending.Clear();
                    _timestamps.Clear();
                }
            }
            var next = _ready.Dequeue();
            using (next.Bitmap) WriteOutput(next.Bitmap, next.Pts, next.Duration, next.Aspect);
            frame = _output;
            return true;
        }

        public void Dispose()
        {
            foreach (var pending in _pending) pending.Dispose();
            _pending.Clear();
            while (_ready.TryDequeue(out var ready)) ready.Bitmap.Dispose();
            _bitmap?.Dispose();
            _bitmap = null;
            _destinationBitmap?.Dispose();
            _destinationBitmap = null;
            var input = _input;
            var reuseInput = _reuseInput;
            var reuseOutput = _reuseOutput;
            ffmpeg.av_frame_free(&reuseInput);
            ffmpeg.av_frame_free(&reuseOutput);
            _reuseInput = _reuseOutput = null;
            _outputPool.Dispose();
            ffmpeg.av_frame_free(&input);
            _output = _input = null;
            if (_converter != null) ffmpeg.sws_freeContext(_converter);
            _converter = null;
            var graph = _graph;
            if (graph != null) ffmpeg.avfilter_graph_free(&graph);
            _graph = null;
        }
    }

}
