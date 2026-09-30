using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FancyToolAva.Services.Lyrics;

/// <summary>Streaming FFmpeg decode to 16 kHz mono float PCM; original files are read only.</summary>
public sealed unsafe class PcmAudioDecoder
{
    public Task DecodeAsync(string source, string output, CancellationToken ct) => Task.Run(() => Decode(source, output, ct), ct);
    private static void Decode(string source, string output, CancellationToken ct)
    {
        if (!FFmpegNative.TryLoad(out var error)) throw new InvalidOperationException("FFmpeg: " + error?.Message, error);
        AVFormatContext* format = null; AVCodecContext* codec = null; SwrContext* resampler = null;
        AVPacket* packet = ffmpeg.av_packet_alloc(); AVFrame* frame = ffmpeg.av_frame_alloc();
        AVChannelLayout mono = default; ffmpeg.av_channel_layout_default(&mono, 1);
        int sourceRate = 0;
        try
        {
            Check(ffmpeg.avformat_open_input(&format, source, null, null), "Open audio");
            Check(ffmpeg.avformat_find_stream_info(format, null), "Read audio streams");
            int stream = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            Check(stream, "No audio stream");
            AVCodec* decoder = ffmpeg.avcodec_find_decoder(format->streams[stream]->codecpar->codec_id);
            if (decoder == null) throw new InvalidDataException("Audio decoder unavailable.");
            codec = ffmpeg.avcodec_alloc_context3(decoder);
            Check(ffmpeg.avcodec_parameters_to_context(codec, format->streams[stream]->codecpar), "Audio parameters");
            Check(ffmpeg.avcodec_open2(codec, decoder, null), "Open decoder");
            using var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read);
            int read;
            while ((read = ffmpeg.av_read_frame(format, packet)) >= 0)
            {
                ct.ThrowIfCancellationRequested();
                if (packet->stream_index == stream)
                {
                    int sent = ffmpeg.avcodec_send_packet(codec, packet);
                    if (sent == ffmpeg.AVERROR(ffmpeg.EAGAIN)) { Drain(codec, frame, &resampler, &mono, &sourceRate, file, ct); sent = ffmpeg.avcodec_send_packet(codec, packet); }
                    Check(sent, "Decode packet"); Drain(codec, frame, &resampler, &mono, &sourceRate, file, ct);
                }
                ffmpeg.av_packet_unref(packet);
            }
            if (read != ffmpeg.AVERROR_EOF) Check(read, "Read audio packet");
            Check(ffmpeg.avcodec_send_packet(codec, null), "Flush decoder"); Drain(codec, frame, &resampler, &mono, &sourceRate, file, ct);
            if (resampler != null) Convert(resampler, sourceRate, null, 0, file);
        }
        finally
        {
            ffmpeg.av_channel_layout_uninit(&mono);
            if (resampler != null) ffmpeg.swr_free(&resampler);
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (packet != null) ffmpeg.av_packet_free(&packet);
            if (codec != null) ffmpeg.avcodec_free_context(&codec);
            if (format != null) ffmpeg.avformat_close_input(&format);
        }
    }

    private static void Drain(AVCodecContext* codec, AVFrame* frame, SwrContext** resampler, AVChannelLayout* mono, int* sourceRate, FileStream file, CancellationToken ct)
    {
        int received;
        while ((received = ffmpeg.avcodec_receive_frame(codec, frame)) >= 0)
        {
            ct.ThrowIfCancellationRequested();
            if (*resampler == null)
            {
                *sourceRate = frame->sample_rate;
                if (*sourceRate <= 0) throw new InvalidDataException("Invalid audio sample rate.");
                if (frame->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
                {
                    int channels = Math.Max(1, frame->ch_layout.nb_channels);
                    ffmpeg.av_channel_layout_uninit(&frame->ch_layout); ffmpeg.av_channel_layout_default(&frame->ch_layout, channels);
                }
                Check(ffmpeg.swr_alloc_set_opts2(resampler, mono, AVSampleFormat.AV_SAMPLE_FMT_FLT, 16000,
                    &frame->ch_layout, (AVSampleFormat)frame->format, *sourceRate, 0, null), "Create resampler");
                Check(ffmpeg.swr_init(*resampler), "Initialize resampler");
            }
            Convert(*resampler, *sourceRate, frame->extended_data, frame->nb_samples, file); ffmpeg.av_frame_unref(frame);
        }
        if (received != ffmpeg.AVERROR_EOF && received != ffmpeg.AVERROR(ffmpeg.EAGAIN)) Check(received, "Decode frame");
    }
    private static void Convert(SwrContext* resampler, int sourceRate, byte** data, int samples, FileStream file)
    {
        int count = (int)ffmpeg.av_rescale_rnd(ffmpeg.swr_get_delay(resampler, sourceRate) + samples, 16000, sourceRate, AVRounding.AV_ROUND_UP) + 32;
        byte* buffer = (byte*)ffmpeg.av_malloc((ulong)count * 4);
        if (buffer == null) throw new OutOfMemoryException();
        try
        {
            int converted = ffmpeg.swr_convert(resampler, &buffer, count, data, samples);
            Check(converted, "Resample audio");
            file.Write(new ReadOnlySpan<byte>(buffer, converted * 4));
        }
        finally { ffmpeg.av_free(buffer); }
    }
    private static void Check(int code, string operation) { if (code < 0) throw FFmpegNative.CreateException(code, operation); }
    public static float[] ReadChunk(string pcm, int startSample, int samples)
    {
        using var stream = new FileStream(pcm, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = (long)startSample * 4;
        int count = (int)Math.Min(samples, (stream.Length - stream.Position) / 4);
        var audio = new float[count]; stream.ReadExactly(MemoryMarshal.AsBytes(audio.AsSpan())); return audio;
    }
}
