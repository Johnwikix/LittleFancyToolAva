using FancyToolAva.Models;
using FancyToolAva.Services;
using FFmpeg.AutoGen;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunEditingCases(VideoTranscodeService service, string source, string workDir)
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        RunPreviewCases(service, workDir);
        RunExtra("preview positions and pixels", () =>
        {
            var first = service.GetPreviewAsync(source, 0.4).GetAwaiter().GetResult()!;
            var last = service.GetPreviewAsync(source, 1.8).GetAwaiter().GetResult()!;
            if (Math.Abs(first.PositionSeconds - 0.4) > 0.101 || Math.Abs(last.PositionSeconds - 1.8) > 0.101)
                throw new InvalidOperationException("Preview does not follow the source timestamp");
            if (first.Pixels.SequenceEqual(last.Pixels) || first.Pixels.Length != first.Width * first.Height * 4)
                throw new InvalidOperationException("Preview pixels are missing or unchanged");
            var ending = service.GetPreviewAsync(source, 2).GetAwaiter().GetResult();
            if (ending == null || ending.PositionSeconds > 2) throw new InvalidOperationException("EOF preview is missing");
        });

        string compressed = Path.Combine(workDir, "editing-bframes.mp4");
        RunExtra("full source baseline with B frames", () =>
        {
            service.TranscodeAsync(source, compressed, new VideoTranscodeOptions { Preset = PresetLevel.Medium }).GetAwaiter().GetResult();
            Verify(compressed, VideoCodec.H264, AudioCodec.Aac);
        });

        foreach (bool twoPass in new[] { false, true })
        {
            RunExtra($"seek, A/V trim and progress (twoPass={twoPass})", () =>
            {
                var previews = new List<VideoPreviewFrame>();
                var progress = new List<double>();
                string output = Path.Combine(workDir, $"editing-trim-{twoPass}.mp4");
                service.TranscodeAsync(compressed, output, new VideoTranscodeOptions
                {
                    TrimStartSeconds = 0.4, TrimEndSeconds = 1.4, TwoPassEnabled = twoPass,
                    RateControl = twoPass ? RateControlMode.Bitrate : RateControlMode.Crf,
                    Preset = PresetLevel.Ultrafast, VideoBitrateKbps = 300
                }, new CaptureProgress<double>(progress), preview: new CaptureProgress<VideoPreviewFrame>(previews)).GetAwaiter().GetResult();
                AssertDuration(service, output, 1000, 120);
                if (previews.Count == 0 || previews.Any(f => f.PositionSeconds < 0.399 || f.PositionSeconds >= 1.4))
                    throw new InvalidOperationException("Progress preview escaped the clip range");
                if (progress.Count == 0 || progress[^1] != 1 || progress.Any(p => p < 0 || p > 1))
                    throw new InvalidOperationException("Invalid conversion progress");
                var sourceFrame = service.GetPreviewAsync(compressed, 0.4).GetAwaiter().GetResult()!;
                var outputFrame = service.GetPreviewAsync(output, 0).GetAwaiter().GetResult()!;
                double difference = sourceFrame.Pixels.Zip(outputFrame.Pixels, (a, b) => Math.Abs(a - b)).Average();
                if (difference > 15) throw new InvalidOperationException($"Wrong initial clip frame: pixel error {difference:F2}");
            });
        }

        RunExtra("sample-accurate audio boundaries", () =>
        {
            string output = Path.Combine(workDir, "editing-samples.mkv");
            service.TranscodeAsync(source, output, new VideoTranscodeOptions
            {
                Container = VideoContainer.Mkv, AudioCodec = AudioCodec.Flac,
                TrimStartSeconds = 0.353, TrimEndSeconds = 1.447, Preset = PresetLevel.Ultrafast
            }).GetAwaiter().GetResult();
            var audio = ReadAudioSamples(output);
            long expected = (long)Math.Ceiling(1.447 * 44100) - (long)Math.Ceiling(0.353 * 44100);
            if (audio.Samples != expected)
                throw new InvalidOperationException($"Audio length: expected {expected} samples, got {audio.Samples}");
        });

        RunExtra("GIF clip and preview", () =>
        {
            string output = Path.Combine(workDir, "editing-trim.gif");
            service.TranscodeAsync(source, output, new VideoTranscodeOptions
            {
                Container = VideoContainer.Gif, VideoCodec = VideoCodec.Gif, AudioCodec = AudioCodec.None,
                TrimStartSeconds = 0.4, TrimEndSeconds = 1.4, GifFps = 10, GifWidth = 120
            }).GetAwaiter().GetResult();
            AssertDuration(service, output, 1000, 110);
            if (service.GetPreviewAsync(output, 0.5).GetAwaiter().GetResult() == null) throw new InvalidOperationException("GIF preview missing");
        });

        RunExtra("head-only and tail-only clips", () =>
        {
            foreach (var (start, end, duration) in new (double, double?, long)[] { (0.5, null, 1500), (0, 1.2, 1200) })
            {
                string output = Path.Combine(workDir, $"editing-one-boundary-{start}.mp4");
                service.TranscodeAsync(source, output, new VideoTranscodeOptions
                {
                    TrimStartSeconds = start, TrimEndSeconds = end, Preset = PresetLevel.Ultrafast
                }).GetAwaiter().GetResult();
                AssertDuration(service, output, duration, 120);
            }
        });

        RunExtra("nonzero source timestamps", () =>
        {
            string offset = Path.Combine(workDir, "editing-offset.ts");
            WriteOffsetSource(compressed, offset);
            var preview = service.GetPreviewAsync(offset, 0.5).GetAwaiter().GetResult()!;
            if (Math.Abs(preview.PositionSeconds - 0.5) > 0.11) throw new InvalidOperationException($"Wrong preview position: {preview.PositionSeconds:F3}s");
            string output = Path.Combine(workDir, "editing-offset-trim.mp4");
            service.TranscodeAsync(offset, output, new VideoTranscodeOptions
            {
                TrimStartSeconds = 0.4, TrimEndSeconds = 1.4, Preset = PresetLevel.Ultrafast
            }).GetAwaiter().GetResult();
            AssertDuration(service, output, 1000, 120);
        });

        RunExtra("invalid ranges and cancellation", () =>
        {
            foreach (var (start, end) in new[] { (-1d, 1d), (1d, 1d), (3d, 4d), (double.NaN, 1d), (0d, double.PositiveInfinity) })
            {
                try
                {
                    service.TranscodeAsync(source, Path.Combine(workDir, "editing-invalid.mp4"), new VideoTranscodeOptions
                    { TrimStartSeconds = start, TrimEndSeconds = end }).GetAwaiter().GetResult();
                    throw new InvalidOperationException("An invalid range was accepted");
                }
                catch (ArgumentException) { }
            }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                service.GetPreviewAsync(source, 0, cancellation.Token).GetAwaiter().GetResult();
                throw new InvalidOperationException("Canceled preview was accepted");
            }
            catch (OperationCanceledException) { }
        });

        Console.WriteLine($"Editing smoke tests: {_runCount} cases, {_failures} failures");
        return _failures == 0 ? 0 : 1;
    }

    private static void AssertDuration(VideoTranscodeService service, string output, long expected, long tolerance)
    {
        var info = service.ProbeAsync(output).GetAwaiter().GetResult()!;
        if (Math.Abs(info.DurationMs - expected) > tolerance)
            throw new InvalidOperationException($"Duration: expected {expected}ms, got {info.DurationMs}ms");
    }

    private sealed class CaptureProgress<T>(List<T> values) : IProgress<T>
    {
        public void Report(T value) => values.Add(value);
    }

    private static unsafe (long Samples, int Rate) ReadAudioSamples(string path)
    {
        AVFormatContext* input = null;
        AVCodecContext* decoder = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        long samples = 0;
        try
        {
            if (ffmpeg.avformat_open_input(&input, path, null, null) < 0) throw new InvalidOperationException("Cannot read test audio");
            ffmpeg.avformat_find_stream_info(input, null);
            int index = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (index < 0) throw new InvalidOperationException("Trimmed audio stream missing");
            var stream = input->streams[index];
            var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            decoder = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(decoder, stream->codecpar);
            ffmpeg.avcodec_open2(decoder, codec, null);
            while (ffmpeg.av_read_frame(input, packet) >= 0)
            {
                if (packet->stream_index == index)
                {
                    ffmpeg.avcodec_send_packet(decoder, packet);
                    while (ffmpeg.avcodec_receive_frame(decoder, frame) >= 0)
                    { samples += frame->nb_samples; ffmpeg.av_frame_unref(frame); }
                }
                ffmpeg.av_packet_unref(packet);
            }
            ffmpeg.avcodec_send_packet(decoder, null);
            while (ffmpeg.avcodec_receive_frame(decoder, frame) >= 0)
            { samples += frame->nb_samples; ffmpeg.av_frame_unref(frame); }
            return (samples, decoder->sample_rate);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            if (decoder != null) ffmpeg.avcodec_free_context(&decoder);
            if (input != null) ffmpeg.avformat_close_input(&input);
        }
    }

    private static unsafe void WriteOffsetSource(string source, string path)
    {
        AVFormatContext* input = null;
        AVFormatContext* output = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            ffmpeg.avformat_open_input(&input, source, null, null);
            ffmpeg.avformat_find_stream_info(input, null);
            ffmpeg.avformat_alloc_output_context2(&output, null, "mpegts", path);
            for (uint i = 0; i < input->nb_streams; i++)
            {
                var stream = ffmpeg.avformat_new_stream(output, null);
                ffmpeg.avcodec_parameters_copy(stream->codecpar, input->streams[i]->codecpar);
                stream->codecpar->codec_tag = 0;
                stream->time_base = input->streams[i]->time_base;
            }
            ffmpeg.avio_open(&output->pb, path, ffmpeg.AVIO_FLAG_WRITE);
            ffmpeg.avformat_write_header(output, null);
            while (ffmpeg.av_read_frame(input, packet) >= 0)
            {
                var src = input->streams[packet->stream_index];
                var dest = output->streams[packet->stream_index];
                ffmpeg.av_packet_rescale_ts(packet, src->time_base, dest->time_base);
                long offset = (long)Math.Round(5 / ffmpeg.av_q2d(dest->time_base));
                if (packet->pts != ffmpeg.AV_NOPTS_VALUE) packet->pts += offset;
                if (packet->dts != ffmpeg.AV_NOPTS_VALUE) packet->dts += offset;
                packet->pos = -1;
                ffmpeg.av_interleaved_write_frame(output, packet);
                ffmpeg.av_packet_unref(packet);
            }
            ffmpeg.av_write_trailer(output);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            if (input != null) ffmpeg.avformat_close_input(&input);
            if (output != null) { ffmpeg.avio_closep(&output->pb); ffmpeg.avformat_free_context(output); }
        }
    }
}
