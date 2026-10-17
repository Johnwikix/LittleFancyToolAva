using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using FFmpeg.AutoGen;
using FancyToolAva.Models;
using SkiaSharp;

namespace FancyToolAva.Services;

public sealed unsafe partial class VideoTranscodeService
{
    /// <summary>只复用没有外部引用的帧；像素内存仍由 FFmpeg 按原生对齐要求分配。</summary>
    private sealed class BgraFramePool : IDisposable
    {
        private readonly List<nint> _frames = new(6);
        private AVFrame* _fallback;

        internal AVFrame* Get(int width, int height)
        {
            long stamp = VideoPipelineMetrics.Start();
            foreach (nint item in _frames)
            {
                var frame = (AVFrame*)item;
                if (frame->width != width || frame->height != height || ffmpeg.av_frame_is_writable(frame) == 0) continue;
                VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.FrameReuse, stamp);
                return frame;
            }
            long bytes = (long)width * height * 4;
            int capacity = (int)Math.Clamp(128L * 1024 * 1024 / Math.Max(1, bytes), 2, 6);
            if (_frames.Count < capacity)
            {
                var frame = ffmpeg.av_frame_alloc();
                if (frame == null) throw new OutOfMemoryException();
                try
                {
                    PrepareBgraOutput(frame, width, height);
                    _frames.Add((nint)frame);
                    return frame;
                }
                catch { ffmpeg.av_frame_free(&frame); throw; }
            }
            // 滤镜内部可能保留更多引用；超过池预算时沿用单帧换缓冲，不增加保留槽位。
            if (_fallback == null) _fallback = ffmpeg.av_frame_alloc();
            if (_fallback == null) throw new OutOfMemoryException();
            PrepareBgraOutput(_fallback, width, height);
            return _fallback;
        }

        public void Dispose()
        {
            foreach (nint item in _frames)
            {
                var frame = (AVFrame*)item;
                ffmpeg.av_frame_free(&frame);
            }
            _frames.Clear();
            var fallback = _fallback;
            ffmpeg.av_frame_free(&fallback);
            _fallback = null;
        }
    }
    private static void WrapBgraFrame(SKBitmap bitmap, AVFrame* frame)
    {
        // Skia 只借用像素。AVFrame 在整个同步处理调用期间持有引用，退出前所有 worker 已 join。
        if (!bitmap.InstallPixels(new SKImageInfo(frame->width, frame->height, SKColorType.Bgra8888, SKAlphaType.Opaque),
            (nint)frame->data[0], frame->linesize[0]))
            throw new InvalidOperationException(LocalizationRegistry.Get("VideoTranscode.Msg_PipelineFailed", "SKBitmap.InstallPixels"));
    }
    private static void PrepareBgraOutput(AVFrame* frame, int width, int height)
    {
        if (frame->width == width && frame->height == height && ffmpeg.av_frame_is_writable(frame) != 0) return;
        // 队列持有上一帧引用时直接换缓冲；make_writable 会先复制即将被全部覆盖的旧像素。
        ffmpeg.av_frame_unref(frame);
        frame->width = width;
        frame->height = height;
        frame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
        long allocateStamp = VideoPipelineMetrics.Start();
        int result = ffmpeg.av_frame_get_buffer(frame, 32);
        if (result < 0) throw FFmpegNative.CreateException(result, LocalizationRegistry.Get("VideoTranscode.Msg_PipelineFailed", "av_frame_get_buffer"));
        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.FrameAllocate, allocateStamp);
    }

    private interface IVideoFrameStage
    {
        void Send(AVFrame* frame);
        bool Receive(CancellationToken ct, out AVFrame* frame);
    }

    /// <summary>每个原生上下文由一个工作线程独占；队列转移 AVFrame/AVPacket 引用的所有权。</summary>
    private sealed class QueuedVideoPipeline : IDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly BlockingCollection<nint>[] _frames;
        private readonly BlockingCollection<nint> _packets = new(64);
        private readonly List<Task> _workers = new(4);
        private readonly List<Task> _videoWorkers = new(3);
        private readonly IVideoFrameStage[] _stages;
        private readonly AVFilterContext* _source;
        private readonly AVFilterContext* _sink;
        private readonly AVCodecContext* _encoder;
        private readonly AVFormatContext* _output;
        private readonly AVStream* _stream;
        private AVFrame* _filterFrame;
        private AVPacket* _packet;
        private readonly double _total;
        private readonly double _trimStart;
        private readonly AVRational _timeBase;
        private readonly IProgress<double>? _progress;
        private readonly PreviewConverter _previewConverter;
        private readonly IProgress<VideoPreviewFrame>? _preview;
        private readonly VideoPipelineMetrics? _metrics;
        private ExceptionDispatchInfo? _failure;
        internal bool UsesCudaFrames => _source == null;
        private bool _videoCompleted;
        private bool _disposed;

        internal QueuedVideoPipeline(VideoSrPipeline? sr, VideoInterpolationPipeline? interpolation, bool upscaleFirst,
            AVFilterContext* source, AVFilterContext* sink, AVCodecContext* encoder, AVFormatContext* output,
            AVStream* stream, double total, IProgress<double>? progress, PreviewConverter previewConverter,
            IProgress<VideoPreviewFrame>? preview, double trimStart, CancellationToken ct, IVideoFrameStage[]? cudaStages = null, AVRational cudaTimeBase = default)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _stages = cudaStages ?? (sr == null ? [interpolation!] : interpolation == null ? [sr]
                : upscaleFirst ? [sr, interpolation] : [interpolation, sr]);
            _source = source;
            _sink = sink;
            _encoder = encoder;
            _output = output;
            _stream = stream;
            _total = total;
            _progress = progress;
            _previewConverter = previewConverter;
            // 单独超分沿用源帧预览；含插帧时预览最终处理帧。
            _preview = interpolation != null || cudaStages?.Any(stage => stage is CudaRifeStage) == true ? preview : null;
            _trimStart = trimStart;
            _timeBase = source == null ? cudaTimeBase : source->outputs[0]->time_base;
            _metrics = VideoPipelineMetrics.Current;
            // 每条队列最多保留约 64 MiB 的图像；超大帧仍允许一个在途引用以维持背压。
            long bytes = Math.Max(source == null ? 0 : (long)source->outputs[0]->w * source->outputs[0]->h * 4,
                (long)encoder->width * encoder->height * 4);
            int capacity = (int)Math.Clamp(64L * 1024 * 1024 / Math.Max(1, bytes), 1, 4);
            _frames = new BlockingCollection<nint>[_stages.Length + 1];
            for (int i = 0; i < _frames.Length; i++) _frames[i] = new(capacity);
            try
            {
                _filterFrame = ffmpeg.av_frame_alloc();
                _packet = ffmpeg.av_packet_alloc();
                if (_filterFrame == null || _packet == null) throw new OutOfMemoryException();
                Start(Mux);
                for (int i = 0; i < _stages.Length; i++)
                {
                    int stage = i;
                    _videoWorkers.Add(Start(() => ProcessStage(stage)));
                }
                _videoWorkers.Add(Start(Encode));
            }
            catch { Dispose(); throw; }
        }

        private Task Start(Action action)
        {
            var task = Task.Factory.StartNew(() =>
            {
                VideoPipelineMetrics.Current = _metrics;
                try { action(); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (Exception error)
                {
                    Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(error), null);
                    RequestStop();
                }
                finally { VideoPipelineMetrics.Current = null; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _workers.Add(task);
            return task;
        }

        internal void ThrowIfFailed() => Volatile.Read(ref _failure)?.Throw();

        private void RequestStop()
        {
            try { _stop.Cancel(); }
            catch (Exception error)
            {
                // 取消回调失败仍须等待工作线程退出并释放所有队列引用。
                Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(error), null);
            }
        }

        internal void Send(AVFrame* frame) => AddFrame(_frames[0], frame, VideoPipelineMetrics.Phase.DecodeQueueWrite);

        private void AddFrame(BlockingCollection<nint> queue, AVFrame* frame, VideoPipelineMetrics.Phase phase)
        {
            ThrowIfFailed();
            var owned = ffmpeg.av_frame_clone(frame);
            if (owned == null) throw new OutOfMemoryException();
            try
            {
                long stamp = VideoPipelineMetrics.Start();
                queue.Add((nint)owned, _stop.Token);
                owned = null;
                VideoPipelineMetrics.Record(phase, stamp);
            }
            finally { ffmpeg.av_frame_free(&owned); }
        }

        private bool TakeFrame(BlockingCollection<nint> queue, VideoPipelineMetrics.Phase phase, out nint frame)
        {
            long stamp = VideoPipelineMetrics.Start();
            bool found = queue.TryTake(out frame, Timeout.Infinite, _stop.Token);
            VideoPipelineMetrics.Record(phase, stamp);
            return found;
        }

        private void ProcessStage(int index)
        {
            var stage = _stages[index];
            bool sr = stage is VideoSrPipeline;
            var read = sr ? VideoPipelineMetrics.Phase.SrQueueRead : VideoPipelineMetrics.Phase.RifeQueueRead;
            var write = sr ? VideoPipelineMetrics.Phase.SrQueueWrite : VideoPipelineMetrics.Phase.RifeQueueWrite;
            void Drain()
            {
                while (stage.Receive(_stop.Token, out var output)) AddFrame(_frames[index + 1], output, write);
            }
            while (TakeFrame(_frames[index], read, out var item))
            {
                var frame = (AVFrame*)item;
                try
                {
                    stage.Send(frame);
                    Drain();
                }
                finally { ffmpeg.av_frame_free(&frame); }
            }
            stage.Send(null);
            Drain();
            _frames[index + 1].CompleteAdding();
        }

        private void Encode()
        {
            while (TakeFrame(_frames[^1], VideoPipelineMetrics.Phase.EncodeQueueRead, out var item))
            {
                var frame = (AVFrame*)item;
                try
                {
                    _previewConverter.Report(frame, _trimStart + frame->pts * ffmpeg.av_q2d(_timeBase), _preview);
                    if (_source == null) EncodeCuda(frame);
                    else PushVideoFrame(_source, _sink, frame, _filterFrame, _encoder, _output, _stream, _packet, _total, _progress, this);
                }
                finally { ffmpeg.av_frame_free(&frame); }
            }
            if (_source != null)
            {
            int filterResult = ffmpeg.av_buffersrc_add_frame(_source, null);
            if (filterResult < 0) throw FFmpegNative.CreateException(filterResult, "CUDA fallback filter flush");
            DrainSinkInto(_sink, _filterFrame, _encoder, _output, _stream, _packet, _total, _progress, this);
            }
            int result = 0;
            using (_source == null ? VideoGpuWorkGate.Enter(_stop.Token) : default)
                result = ffmpeg.avcodec_send_frame(_encoder, null);
            if (result < 0) throw FFmpegNative.CreateException(result, LocalizationRegistry.Get("VideoTranscode.Msg_PipelineFailed", "avcodec_send_frame/flush"));
            DrainPackets(_encoder, _output, _stream, _packet, _encoder->time_base, this);
        }

        private void EncodeCuda(AVFrame* frame)
        {
            long stamp = VideoPipelineMetrics.Start();
            SetFrameColor(frame, _encoder);
            frame->pts = ffmpeg.av_rescale_q(frame->pts, _timeBase, _encoder->time_base);
            if (frame->duration > 0) frame->duration = ffmpeg.av_rescale_q(frame->duration, _timeBase, _encoder->time_base);
            int result;
            using (VideoGpuWorkGate.Enter(_stop.Token)) result = ffmpeg.avcodec_send_frame(_encoder, frame);
            if (result < 0) throw FFmpegNative.CreateException(result, "CUDA NVENC frame");
            if (_total > 0) _progress?.Report(Math.Clamp(frame->pts * ffmpeg.av_q2d(_encoder->time_base) / _total, 0, 1));
            DrainPackets(_encoder, _output, _stream, _packet, _encoder->time_base, this);
            (_progress as EncodingProgress)?.ReportFrame();
            VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.Encode, stamp);
        }

        internal void SendPacket(AVPacket* packet)
        {
            ThrowIfFailed();
            var owned = ffmpeg.av_packet_clone(packet);
            if (owned == null) throw new OutOfMemoryException();
            try
            {
                _packets.Add((nint)owned, _stop.Token);
                owned = null;
            }
            finally { ffmpeg.av_packet_free(&owned); }
        }

        private void Mux()
        {
            foreach (nint item in _packets.GetConsumingEnumerable(_stop.Token))
            {
                var packet = (AVPacket*)item;
                try
                {
                    int result = ffmpeg.av_interleaved_write_frame(_output, packet);
                    if (result < 0) throw FFmpegNative.CreateException(result, LocalizationRegistry.Get("VideoTranscode.Msg_PipelineFailed", "av_interleaved_write_frame"));
                }
                finally { ffmpeg.av_packet_free(&packet); }
            }
        }

        internal void CompleteVideo()
        {
            if (_videoCompleted) return;
            _videoCompleted = true;
            _frames[0].CompleteAdding();
        }

        internal void Finish()
        {
            CompleteVideo();
            Task.WaitAll(_videoWorkers.ToArray());
            ThrowIfFailed();
            _stop.Token.ThrowIfCancellationRequested();
            _packets.CompleteAdding();
            Task.WaitAll(_workers.ToArray());
            ThrowIfFailed();
            _stop.Token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RequestStop();
            Task.WaitAll(_workers.ToArray());
            foreach (var queue in _frames)
            {
                while (queue.TryTake(out nint item))
                {
                    var frame = (AVFrame*)item;
                    ffmpeg.av_frame_free(&frame);
                }
                queue.Dispose();
            }
            while (_packets.TryTake(out nint item))
            {
                var packet = (AVPacket*)item;
                ffmpeg.av_packet_free(&packet);
            }
            _packets.Dispose();
            _stop.Dispose();
            var filter = _filterFrame;
            var encoded = _packet;
            ffmpeg.av_frame_free(&filter);
            ffmpeg.av_packet_free(&encoded);
            _filterFrame = null;
            _packet = null;
        }
    }
}
