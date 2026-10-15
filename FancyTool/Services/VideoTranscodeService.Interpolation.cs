using System.Globalization;
using FancyToolAva.Models;
using FFmpeg.AutoGen;
using SkiaSharp;

namespace FancyToolAva.Services;

public sealed unsafe partial class VideoTranscodeService
{
    private static double InterpolationRate(VideoTranscodeOptions options, double sourceFps)
        => options.Container == VideoContainer.Gif ? options.GifFps
            : options.InterpolationMultiplier == 0 ? NormalizeInterpolationRate(options.FpsValue)
            : (sourceFps > 0 ? sourceFps : 30) * options.InterpolationMultiplier;

    private static double NormalizeInterpolationRate(double rate) => rate switch
    {
        var value when Math.Abs(value - 23.976) < 0.000001 => 24000d / 1001,
        var value when Math.Abs(value - 29.97) < 0.000001 => 30000d / 1001,
        var value when Math.Abs(value - 59.94) < 0.000001 => 60000d / 1001,
        var value when Math.Abs(value - 119.88) < 0.000001 => 120000d / 1001,
        _ => rate
    };

    private static void ValidateInterpolation(VideoTranscodeOptions options, VideoProbeInfo source)
    {
        if (options.InterpolationMode == VideoInterpolationMode.None) return;
        double rate = InterpolationRate(options, source.Fps);
        if (!Enum.IsDefined(options.InterpolationMode) || !Enum.IsDefined(options.FrameProcessingOrder) || options.InterpolationMultiplier is not (0 or 2 or 4)
            || !double.IsFinite(rate) || rate < 1 || rate > 240)
            throw new ArgumentException(LocalizationRegistry.Get("VideoTranscode.Interp_InvalidRate"));
        if (source.IsHdr && options.HdrOutput != VideoHdrOutput.ForceSdr && options.Container != VideoContainer.Gif)
            throw new NotSupportedException(LocalizationRegistry.Get("VideoTranscode.Interp_HdrUnsupported"));
    }

    private static bool UpscaleBeforeInterpolation(VideoTranscodeOptions options, AVCodecContext* decoder)
    {
        if (!options.SuperResolutionEnabled || options.InterpolationMode == VideoInterpolationMode.None) return false;
        if (options.FrameProcessingOrder != VideoFrameProcessingOrder.Auto)
            return options.FrameProcessingOrder == VideoFrameProcessingOrder.UpscaleThenInterpolation;
        long width = decoder->width - (options.CropEnabled ? options.CropLeft + options.CropRight : 0);
        long height = decoder->height - (options.CropEnabled ? options.CropTop + options.CropBottom : 0);
        // Larger optical-flow frames grow the working set sharply. At modest
        // sizes, enhancing originals only avoids repeating SR for generated frames.
        return options.InterpolationMode == VideoInterpolationMode.Rife
            && width * height * options.SuperResolutionScale * options.SuperResolutionScale <= 1920L * 1080;
    }

    private static AVCodecContext ProcessedDecoder(int width, int height, AVRational aspect) => new()
    {
        width = width, height = height, pix_fmt = AVPixelFormat.AV_PIX_FMT_BGRA,
        sample_aspect_ratio = aspect, color_primaries = AVColorPrimaries.AVCOL_PRI_BT709,
        color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709, colorspace = AVColorSpace.AVCOL_SPC_RGB,
        color_range = AVColorRange.AVCOL_RANGE_JPEG
    };

    /// <summary>Samples frame pairs on a rational output clock while retaining only two source frames.</summary>
    private sealed class VideoInterpolationPipeline : IDisposable
    {
        private AVFilterGraph* _graph;
        private AVFilterContext* _source;
        private AVFilterContext* _sink;
        private AVFrame* _input = ffmpeg.av_frame_alloc();
        private AVFrame* _output = ffmpeg.av_frame_alloc();
        private IVideoFrameInterpolator? _model;
        private SKBitmap? _previous;
        private SKBitmap? _current;
        private SKBitmap? _intermediate;
        private readonly bool _neural;
        private readonly bool _detectScenes;
        private readonly double _clipDuration;
        private double _lastDuration;
        private double _previousTime;
        private double _currentTime;
        private long _nextFrame;
        private bool _hasPrevious;
        private bool _hasCurrent;
        private bool _holdPair;
        private bool _eof;
        private bool _ended;
        private bool _submittedFirst;

        public int Width => _sink->inputs[0]->w;
        public int Height => _sink->inputs[0]->h;
        public AVRational Aspect => _sink->inputs[0]->sample_aspect_ratio;
        public AVRational TimeBase { get; }
        public bool ScaleApplied { get; }

        public VideoInterpolationPipeline(AVCodecContext* decoder, AVStream* stream, VideoTranscodeOptions options,
            double sourceFps, long durationMs, IVideoInterpolationService? service, CancellationToken ct, bool preprocessed = false)
        {
            _neural = options.InterpolationMode == VideoInterpolationMode.Rife;
            _detectScenes = options.InterpolationSceneDetection;
            _clipDuration = Math.Min(options.TrimEndSeconds ?? double.PositiveInfinity,
                durationMs > 0 ? durationMs / 1000d : double.PositiveInfinity) - options.TrimStartSeconds;
            _lastDuration = 1 / (sourceFps > 0 ? sourceFps : 30);
            var rate = ffmpeg.av_d2q(InterpolationRate(options, sourceFps), 1_001_000);
            TimeBase = new AVRational { num = rate.den, den = rate.num };
            try
            {
                _graph = ffmpeg.avfilter_graph_alloc();
                if (_graph == null || _input == null || _output == null) throw new OutOfMemoryException();
                _source = CreateBufferSource(_graph, decoder, stream);
                _sink = CreateBufferSink(_graph);
                var pre = options.WithTrim(0, null);
                pre.Container = VideoContainer.Mp4;
                pre.HardwareBackend = HardwareBackend.Software;
                pre.ScaleMode = ScaleMode.None;
                if (preprocessed) { pre.CropEnabled = false; pre.Deinterlace = DeinterlaceMode.None; pre.Denoise = DenoiseMode.None; }
                // Explicit downscaling avoids running optical flow on pixels the
                // output will discard. Upscaling remains after interpolation.
                if (!options.SuperResolutionEnabled)
                {
                    int width = decoder->width - (options.CropEnabled ? options.CropLeft + options.CropRight : 0);
                    int height = decoder->height - (options.CropEnabled ? options.CropTop + options.CropBottom : 0);
                    bool downscale = options.ScaleMode switch
                    {
                        ScaleMode.FitWithin => options.ScaleWidth < width || options.ScaleHeight < height,
                        ScaleMode.Exact => options.ScaleWidth <= width && options.ScaleHeight <= height
                            && (options.ScaleWidth < width || options.ScaleHeight < height),
                        ScaleMode.Width => options.ScaleWidth < width,
                        ScaleMode.Height => options.ScaleHeight < height,
                        _ => false
                    };
                    if (downscale) { pre.ScaleMode = options.ScaleMode; ScaleApplied = true; }
                    if (options.Container == VideoContainer.Gif && options.GifWidth > 0 && options.GifWidth < width)
                    { pre.ScaleMode = ScaleMode.Width; pre.ScaleWidth = options.GifWidth; ScaleApplied = true; }
                }
                pre.FpsMode = FpsMode.SameAsSource;
                pre.InterpolationMode = VideoInterpolationMode.None;
                string description = BuildFilterDescription(pre, _neural ? "bgra" : "yuv444p",
                    BuildColorFilters(ReadColor(decoder), _neural ? VideoColor.SdrRgb : VideoColor.Sdr));
                if (!_neural)
                {
                    // Supply lookahead at EOF, then trim the generated clock to the clip.
                    string padding = (3 * _lastDuration).ToString("R", CultureInfo.InvariantCulture);
                    description += $",tpad=stop_mode=clone:stop_duration={padding},minterpolate=fps={rate.num}/{rate.den}:mi_mode=mci:mc_mode=aobmc:me_mode=bilat:me=epzs:vsbmc=1:scd={(_detectScenes ? "fdiff" : "none")}";
                    if (double.IsFinite(_clipDuration)) description += ",trim=duration=" + _clipDuration.ToString("R", CultureInfo.InvariantCulture);
                    description += "," + BuildColorFilters(VideoColor.Sdr, VideoColor.SdrRgb) + ",format=bgra";
                }
                AVBufferRef* unused = null;
                if (!BindGraphAndConfigure(_graph, _source, _sink, description, false, "", &unused))
                    throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Interp_PreprocessingFailed"));
                _output->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                _output->width = Width; _output->height = Height;
                int ret = ffmpeg.av_frame_get_buffer(_output, 32);
                if (ret < 0) throw FFmpegNative.CreateException(ret, LocalizationRegistry.Get("VideoTranscode.Interp_PreprocessingFailed"));
                if (_neural)
                {
                    _previous = Bitmap(); _current = Bitmap(); _intermediate = Bitmap();
                    _model = (service ?? throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Interp_ServiceUnavailable")))
                        .CreateSessionAsync(options, Width, Height, ct).GetAwaiter().GetResult();
                }
            }
            catch { Dispose(); throw; }
        }

        private SKBitmap Bitmap() => new(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque));

        public void Send(AVFrame* frame)
        {
            if (frame != null && !_submittedFirst)
            {
                _submittedFirst = true;
                // A non-frame-aligned trim can start before the first retained
                // picture. Hold that picture from zero without shifting later PTS.
                if (!_neural && frame->pts > 0)
                {
                    var first = ffmpeg.av_frame_clone(frame);
                    if (first == null) throw new OutOfMemoryException();
                    try
                    {
                        first->duration = frame->pts;
                        first->pts = 0;
                        int initial = ffmpeg.av_buffersrc_add_frame_flags(_source, first, (int)AvBuffersrcFlag.AV_BUFFERSRC_FLAG_KEEP_REF);
                        if (initial < 0) throw FFmpegNative.CreateException(initial, LocalizationRegistry.Get("VideoTranscode.Interp_PreprocessingFailed"));
                    }
                    finally { ffmpeg.av_frame_free(&first); }
                }
            }
            int ret = ffmpeg.av_buffersrc_add_frame_flags(_source, frame, (int)AvBuffersrcFlag.AV_BUFFERSRC_FLAG_KEEP_REF);
            if (ret < 0) throw FFmpegNative.CreateException(ret, LocalizationRegistry.Get("VideoTranscode.Interp_PreprocessingFailed"));
        }

        public bool Receive(CancellationToken ct, out AVFrame* frame)
        {
            frame = null;
            if (_ended) return false;
            ct.ThrowIfCancellationRequested();
            if (!_neural)
            {
                ffmpeg.av_frame_unref(_input);
                int ret = ffmpeg.av_buffersink_get_frame(_sink, _input);
                if (ret == ErrEagain || ret == ffmpeg.AVERROR_EOF) return false;
                if (ret < 0) throw FFmpegNative.CreateException(ret, LocalizationRegistry.Get("VideoTranscode.Interp_PreprocessingFailed"));
                _input->pts = ffmpeg.av_rescale_q(_input->pts, _sink->inputs[0]->time_base, TimeBase);
                _input->duration = 1;
                frame = _input;
                return true;
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                double time = _nextFrame * ffmpeg.av_q2d(TimeBase);
                if (time >= _clipDuration - 0.0000001) { _ended = true; return false; }
                if (_hasCurrent)
                {
                    if (time < _currentTime - 0.0000001)
                    {
                        var bitmap = _previous!;
                        if (!_holdPair && time > _previousTime + 0.0000001)
                        {
                            _model!.Interpolate(Math.Clamp((time - _previousTime) / (_currentTime - _previousTime), 0.00000001, 0.99999999), _intermediate!, ct);
                            bitmap = _intermediate!;
                        }
                        frame = Output(bitmap);
                        return true;
                    }
                    (_previous, _current) = (_current, _previous);
                    _previousTime = _currentTime;
                    _hasCurrent = false;
                }
                if (_eof)
                {
                    double end = double.IsFinite(_clipDuration) ? _clipDuration : _previousTime + _lastDuration;
                    if (!_hasPrevious || time >= end - 0.0000001) { _ended = true; return false; }
                    frame = Output(_previous!);
                    return true;
                }
                ffmpeg.av_frame_unref(_input);
                int ret = ffmpeg.av_buffersink_get_frame(_sink, _input);
                if (ret == ErrEagain) return false;
                if (ret == ffmpeg.AVERROR_EOF) { _eof = true; continue; }
                if (ret < 0) throw FFmpegNative.CreateException(ret, LocalizationRegistry.Get("VideoTranscode.Interp_PreprocessingFailed"));
                double position = _input->pts * ffmpeg.av_q2d(_sink->inputs[0]->time_base);
                if (_hasPrevious && position <= _previousTime + 0.0000001) continue;
                if (_input->duration > 0) _lastDuration = _input->duration * ffmpeg.av_q2d(_sink->inputs[0]->time_base);
                else if (_hasPrevious) _lastDuration = position - _previousTime;
                CopyInput(_hasPrevious ? _current! : _previous!);
                if (!_hasPrevious) { _hasPrevious = true; _previousTime = position; continue; }
                _currentTime = position;
                _hasCurrent = true;
                _holdPair = SamePixels(_previous!, _current!) || (_detectScenes && IsSceneChange(_previous!, _current!));
                if (!_holdPair) _model!.SetFrames(_previous!, _current!, ct);
            }
        }

        private void CopyInput(SKBitmap bitmap)
        {
            for (int y = 0; y < Height; y++)
                new ReadOnlySpan<byte>(_input->data[0] + y * _input->linesize[0], Width * 4)
                    .CopyTo(new Span<byte>((byte*)bitmap.GetPixels() + y * bitmap.RowBytes, Width * 4));
        }

        private AVFrame* Output(SKBitmap bitmap)
        {
            int ret = ffmpeg.av_frame_make_writable(_output);
            if (ret < 0) throw FFmpegNative.CreateException(ret, LocalizationRegistry.Get("VideoTranscode.Interp_PreprocessingFailed"));
            for (int y = 0; y < Height; y++)
                new ReadOnlySpan<byte>((byte*)bitmap.GetPixels() + y * bitmap.RowBytes, Width * 4)
                    .CopyTo(new Span<byte>(_output->data[0] + y * _output->linesize[0], Width * 4));
            _output->pts = _nextFrame++;
            _output->duration = 1;
            _output->sample_aspect_ratio = Aspect;
            _output->time_base = TimeBase;
            return _output;
        }

        private static bool SamePixels(SKBitmap first, SKBitmap second)
            => first.GetPixelSpan().SequenceEqual(second.GetPixelSpan());

        private static bool IsSceneChange(SKBitmap first, SKBitmap second)
        {
            Span<int> histogram0 = stackalloc int[32];
            Span<int> histogram1 = stackalloc int[32];
            histogram0.Clear(); histogram1.Clear();
            long difference = 0;
            int count = 0;
            for (int y = 0; y < first.Height; y += Math.Max(1, first.Height / 36))
            for (int x = 0; x < first.Width; x += Math.Max(1, first.Width / 64))
            {
                byte* a = (byte*)first.GetPixels() + y * first.RowBytes + x * 4;
                byte* b = (byte*)second.GetPixels() + y * second.RowBytes + x * 4;
                int luma0 = (a[0] * 29 + a[1] * 150 + a[2] * 77) >> 8;
                int luma1 = (b[0] * 29 + b[1] * 150 + b[2] * 77) >> 8;
                histogram0[luma0 >> 3]++; histogram1[luma1 >> 3]++;
                difference += Math.Abs(luma0 - luma1); count++;
            }
            int distance = 0;
            for (int i = 0; i < 32; i++) distance += Math.Abs(histogram0[i] - histogram1[i]);
            return difference > count * 255 * 0.55 || (difference > count * 255 * 0.18 && distance > count * 0.8);
        }

        public void Dispose()
        {
            _model?.Dispose(); _model = null;
            _previous?.Dispose(); _current?.Dispose(); _intermediate?.Dispose();
            _previous = _current = _intermediate = null;
            var input = _input; var output = _output; var graph = _graph;
            ffmpeg.av_frame_free(&input); ffmpeg.av_frame_free(&output); ffmpeg.avfilter_graph_free(&graph);
            _input = _output = null; _graph = null;
        }
    }

    private static void DrainInterpolatedFrames(VideoInterpolationPipeline pipeline, VideoSrPipeline? enhancement, CancellationToken ct,
        AVFilterContext* source, AVFilterContext* sink, AVFrame* filterFrame, AVCodecContext* encoder,
        AVFormatContext* output, AVStream* stream, AVPacket* packet, double total, IProgress<double>? progress,
        PreviewConverter converter, IProgress<VideoPreviewFrame>? preview, double trimStart)
    {
        while (pipeline.Receive(ct, out var frame))
        {
            if (enhancement != null)
            {
                enhancement.Send(frame);
                DrainEnhancedFrames(enhancement, ct, source, sink, filterFrame, encoder, output, stream, packet, total, progress, converter, preview, trimStart);
            }
            else
            {
                converter.Report(frame, trimStart + frame->pts * ffmpeg.av_q2d(pipeline.TimeBase), preview);
                PushVideoFrame(source, sink, frame, filterFrame, encoder, output, stream, packet, total, progress);
            }
        }
    }

    private static void DrainEnhancedBeforeInterpolation(VideoSrPipeline enhancement, VideoInterpolationPipeline interpolation, CancellationToken ct,
        AVFilterContext* source, AVFilterContext* sink, AVFrame* filterFrame, AVCodecContext* encoder,
        AVFormatContext* output, AVStream* stream, AVPacket* packet, double total, IProgress<double>? progress,
        PreviewConverter converter, IProgress<VideoPreviewFrame>? preview, double trimStart)
    {
        while (enhancement.Receive(ct, out var frame))
        {
            interpolation.Send(frame);
            DrainInterpolatedFrames(interpolation, null, ct, source, sink, filterFrame, encoder, output, stream,
                packet, total, progress, converter, preview, trimStart);
        }
    }
}
