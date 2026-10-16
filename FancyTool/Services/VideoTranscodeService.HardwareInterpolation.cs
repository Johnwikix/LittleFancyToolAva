using FFmpeg.AutoGen;

namespace FancyToolAva.Services;

public sealed unsafe partial class VideoTranscodeService
{
    /// <summary>Initializes the actual vendor filter and requires it to generate frames on the selected adapter.</summary>
    internal static void ProbeHardwareInterpolation(VideoInterpolationMode mode, VideoGpuAdapter adapter, int multiplier)
    {
        AVFilterGraph* graph = null;
        AVBufferRef* device = null;
        AVFrame* input = null;
        AVFrame* output = null;
        try
        {
            device = HardwareVideoInterpolation.CreateDevice(mode, adapter);
            graph = ffmpeg.avfilter_graph_alloc();
            input = ffmpeg.av_frame_alloc();
            output = ffmpeg.av_frame_alloc();
            if (graph == null || input == null || output == null) throw new OutOfMemoryException();
            var decoder = ProcessedDecoder(320, 192, new AVRational { num = 1, den = 1 });
            var stream = new AVStream { time_base = new AVRational { num = 1, den = 24 }, avg_frame_rate = new AVRational { num = 24, den = 1 } };
            var source = CreateBufferSource(graph, &decoder, &stream);
            var sink = CreateBufferSink(graph);
            AVBufferRef* unused = null;
            string description = "format=bgra," + HardwareVideoInterpolation.Filter(mode, multiplier, (24 * multiplier).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!BindGraphAndConfigure(graph, source, sink, description, false, "", &unused, device))
                throw new NotSupportedException(LocalizationRegistry.Get("VideoTranscode.Interp_ProbeFailed"));
            input->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            input->width = 320;
            input->height = 192;
            int result = ffmpeg.av_frame_get_buffer(input, 32);
            if (result < 0) throw FFmpegNative.CreateException(result, "Optical flow probe allocation");
            int generated = 0;
            long lastTimestamp = ffmpeg.AV_NOPTS_VALUE;
            for (int frame = 0; frame < 8; frame++)
            {
                result = ffmpeg.av_frame_make_writable(input);
                if (result < 0) throw FFmpegNative.CreateException(result, "Optical flow probe frame");
                for (int y = 0; y < input->height; y++)
                {
                    var pixels = new Span<byte>(input->data[0] + y * input->linesize[0], input->width * 4);
                    pixels.Clear();
                    for (int x = 0; x < input->width; x++)
                    {
                        pixels[x * 4 + 3] = 255;
                        if (x >= 20 + frame * 4 && x < 60 + frame * 4 && y >= 48 && y < 96)
                            pixels.Slice(x * 4, 3).Fill(200);
                    }
                }
                input->pts = frame;
                input->duration = 1;
                result = ffmpeg.av_buffersrc_add_frame_flags(source, input, (int)AvBuffersrcFlag.AV_BUFFERSRC_FLAG_KEEP_REF);
                if (result < 0) throw FFmpegNative.CreateException(result, "Optical flow probe input");
                while ((result = ffmpeg.av_buffersink_get_frame(sink, output)) >= 0)
                { CheckHardwareProbeFrame(output, ref lastTimestamp); generated++; ffmpeg.av_frame_unref(output); }
                if (result != ErrEagain && result != ffmpeg.AVERROR_EOF) throw FFmpegNative.CreateException(result, "Optical flow probe output");
            }
            result = ffmpeg.av_buffersrc_add_frame_flags(source, null, 0);
            if (result < 0) throw FFmpegNative.CreateException(result, "Optical flow probe flush");
            long deadline = Environment.TickCount64 + 10_000;
            while (true)
            {
                result = ffmpeg.av_buffersink_get_frame(sink, output);
                if (result == ffmpeg.AVERROR_EOF) break;
                if (result == ErrEagain)
                {
                    if (Environment.TickCount64 >= deadline) throw new TimeoutException("Optical flow probe timed out");
                    Thread.Sleep(1);
                    continue;
                }
                if (result < 0) throw FFmpegNative.CreateException(result, "Optical flow probe output");
                CheckHardwareProbeFrame(output, ref lastTimestamp);
                generated++;
                ffmpeg.av_frame_unref(output);
            }
            if (generated < 6 * multiplier) throw new NotSupportedException("Optical flow filter did not generate the requested multiplier");
        }
        finally
        {
            ffmpeg.av_frame_free(&input);
            ffmpeg.av_frame_free(&output);
            ffmpeg.avfilter_graph_free(&graph);
            ffmpeg.av_buffer_unref(&device);
        }
    }

    private static void CheckHardwareProbeFrame(AVFrame* frame, ref long lastTimestamp)
    {
        if (frame->format != (int)AVPixelFormat.AV_PIX_FMT_BGRA || frame->width != 320 || frame->height != 192)
            throw new NotSupportedException("Optical flow probe returned incompatible frames");
        if (frame->pts == ffmpeg.AV_NOPTS_VALUE || lastTimestamp != ffmpeg.AV_NOPTS_VALUE && frame->pts <= lastTimestamp)
            throw new NotSupportedException("Optical flow probe returned an invalid frame clock");
        lastTimestamp = frame->pts;
    }
}
