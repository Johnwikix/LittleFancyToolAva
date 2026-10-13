using System.Diagnostics;
using System.Runtime.InteropServices;
using FancyToolAva.Models;
using FFmpeg.AutoGen;

namespace FancyToolAva.Services;

public sealed unsafe partial class VideoTranscodeService
{
    private sealed record PreviewReadState(CancellationToken Token, long Deadline);

    private static readonly AVIOInterruptCB_callback PreviewInterrupt = static opaque =>
    {
        var state = (PreviewReadState)GCHandle.FromIntPtr((nint)opaque).Target!;
        return state.Token.IsCancellationRequested || Stopwatch.GetTimestamp() >= state.Deadline ? 1 : 0;
    };

    /// <inheritdoc />
    public Task<VideoPreviewFrame?> GetPreviewAsync(string inputPath, double positionSeconds, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(positionSeconds);
        if (!double.IsFinite(positionSeconds)) throw new ArgumentOutOfRangeException(nameof(positionSeconds));
        return Task.Run(() => ReadPreview(inputPath, positionSeconds, ct), ct);
    }

    private static VideoPreviewFrame? ReadPreview(string inputPath, double position, CancellationToken ct)
    {
        if (!FFmpegNative.TryLoad(out var error)) throw new InvalidOperationException("FFmpeg unavailable", error);
        AVFormatContext* input = ffmpeg.avformat_alloc_context();
        AVCodecContext* decoder = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        AVFrame* lastFrame = ffmpeg.av_frame_alloc();
        AVDictionary* options = null;
        var handle = GCHandle.Alloc(new PreviewReadState(ct, Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency));
        using var converter = new PreviewConverter();
        try
        {
            if (input == null || packet == null || frame == null || lastFrame == null) throw new OutOfMemoryException();
            input->interrupt_callback = new AVIOInterruptCB { callback = PreviewInterrupt, opaque = (void*)GCHandle.ToIntPtr(handle) };
            ffmpeg.av_dict_set(&options, "protocol_whitelist", "file", 0);
            int ret = ffmpeg.avformat_open_input(&input, inputPath, null, &options);
            ct.ThrowIfCancellationRequested();
            if (ret < 0) throw FFmpegNative.CreateException(ret, "Cannot open preview source");
            ret = ffmpeg.avformat_find_stream_info(input, null);
            ct.ThrowIfCancellationRequested();
            if (ret < 0) throw FFmpegNative.CreateException(ret, "Cannot read preview source");
            int index = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (index < 0) return null;
            AVStream* stream = input->streams[index];
            decoder = OpenDecoder(stream);
            decoder->pkt_timebase = stream->time_base;
            double origin = GetTimelineOrigin(input, stream);
            double fps = ffmpeg.av_q2d(stream->avg_frame_rate);
            double step = 1 / (fps > 0 ? fps : 30);
            double lastPosition = 0;
            double fallbackPosition = origin;
            if (position > 0)
            {
                double seekPosition = Math.Max(0, position - 1);
                long target = (long)Math.Round((origin + seekPosition) / ffmpeg.av_q2d(stream->time_base));
                ret = ffmpeg.av_seek_frame(input, index, target, ffmpeg.AVSEEK_FLAG_BACKWARD);
                if (ret >= 0) fallbackPosition = origin + seekPosition;
            }

            bool flushing = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                ret = ffmpeg.avcodec_receive_frame(decoder, frame);
                if (ret >= 0)
                {
                    long pts = frame->best_effort_timestamp;
                    double seconds = pts == ffmpeg.AV_NOPTS_VALUE ? fallbackPosition : pts * ffmpeg.av_q2d(stream->time_base);
                    fallbackPosition = seconds + step;
                    lastPosition = Math.Max(0, seconds - origin);
                    if (lastPosition + 0.000001 >= position)
                        return converter.Convert(frame, lastPosition);
                    ffmpeg.av_frame_unref(lastFrame);
                    ffmpeg.av_frame_ref(lastFrame, frame);
                    ffmpeg.av_frame_unref(frame);
                    continue;
                }
                if (ret == ffmpeg.AVERROR_EOF || flushing) break;
                if (ret != ErrEagain) throw FFmpegNative.CreateException(ret, "Preview decode failed");
                do
                {
                    ret = ffmpeg.av_read_frame(input, packet);
                    ct.ThrowIfCancellationRequested();
                    if (ret < 0) break;
                    if (packet->stream_index == index) break;
                    ffmpeg.av_packet_unref(packet);
                } while (true);
                if (ret < 0)
                {
                    CheckReadError(ret, inputPath);
                    ffmpeg.avcodec_send_packet(decoder, null);
                    flushing = true;
                }
                else
                {
                    ret = ffmpeg.avcodec_send_packet(decoder, packet);
                    ffmpeg.av_packet_unref(packet);
                    if (ret < 0) throw FFmpegNative.CreateException(ret, "Preview decode failed");
                }
            }
            return lastFrame->width > 0 ? converter.Convert(lastFrame, lastPosition) : null;
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
            ffmpeg.av_frame_free(&lastFrame);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            if (decoder != null) ffmpeg.avcodec_free_context(&decoder);
            if (input != null) ffmpeg.avformat_close_input(&input);
            handle.Free();
        }
    }

    private static double GetTimelineOrigin(AVFormatContext* input, AVStream* video)
        => input->start_time != ffmpeg.AV_NOPTS_VALUE ? input->start_time / (double)ffmpeg.AV_TIME_BASE
            : video->start_time != ffmpeg.AV_NOPTS_VALUE ? video->start_time * ffmpeg.av_q2d(video->time_base) : 0;

    /// <summary>Reuses the scaling context and limits progress previews to two small frames per second.</summary>
    private sealed class PreviewConverter : IDisposable
    {
        private SwsContext* _scale;
        private long _lastReport;
        private readonly byte*[] _source = new byte*[8];
        private readonly int[] _sourceStride = new int[8];
        private readonly byte*[] _target = new byte*[4];
        private readonly int[] _targetStride = new int[4];

        public void Report(AVFrame* frame, double position, IProgress<VideoPreviewFrame>? preview)
        {
            if (preview == null) return;
            long now = Stopwatch.GetTimestamp();
            if (_lastReport != 0 && now - _lastReport < Stopwatch.Frequency / 2) return;
            _lastReport = now;
            // Preview failures must not abort a successful conversion.
            try { preview.Report(Convert(frame, position)); } catch { }
        }

        public VideoPreviewFrame Convert(AVFrame* frame, double position)
        {
            double aspect = frame->sample_aspect_ratio.num > 0 && frame->sample_aspect_ratio.den > 0
                ? ffmpeg.av_q2d(frame->sample_aspect_ratio) : 1;
            double displayWidth = frame->width * aspect;
            double ratio = Math.Min(1, Math.Min(640 / displayWidth, 360.0 / frame->height));
            int width = Math.Max(1, (int)Math.Round(displayWidth * ratio));
            int height = Math.Max(1, (int)Math.Round(frame->height * ratio));
            _scale = ffmpeg.sws_getCachedContext(_scale, frame->width, frame->height, (AVPixelFormat)frame->format,
                width, height, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
            if (_scale == null) throw new InvalidOperationException("Cannot scale preview frame");
            byte[] pixels = new byte[checked(width * height * 4)];
            for (uint i = 0; i < 8; i++)
            {
                _source[i] = frame->data[i];
                _sourceStride[i] = frame->linesize[i];
            }
            fixed (byte* data = pixels)
            {
                _target[0] = data;
                _targetStride[0] = width * 4;
                int ret = ffmpeg.sws_scale(_scale, _source, _sourceStride, 0, frame->height, _target, _targetStride);
                if (ret < 0) throw FFmpegNative.CreateException(ret, "Cannot scale preview frame");
            }
            return new VideoPreviewFrame(pixels, width, height, position);
        }

        public void Dispose()
        {
            if (_scale != null) ffmpeg.sws_freeContext(_scale);
            _scale = null;
        }
    }
}
