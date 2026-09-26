using FFmpeg.AutoGen;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiagProbe;

/// <summary>用进程内 FFmpeg 解出指定媒体文件的流参数，并解码前几帧对比解码器上下文与实际帧参数。</summary>
internal static unsafe class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--convert")
        {
            // 直接用应用服务复现转换失败：--convert <input> <output> [mp3|m4a|flac...]
            var svc = new AudioConvertService(NullLogger<AudioConvertService>.Instance);
            string input = args[1];
            string output = args[2];
            var fmt = (AudioConvertFormat)Enum.Parse(typeof(AudioConvertFormat), args.Length > 3 ? args[3] : "AacM4a");
            try
            {
                svc.ConvertAsync(input, output, new AudioConvertOptions { Format = fmt }).GetAwaiter().GetResult();
                Console.WriteLine($"CONVERTED OK -> {output} ({new FileInfo(output).Length:N0} bytes)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CONVERT FAILED: {ex.GetType().Name}: {ex.Message}");
            }
            return 0;
        }

        if (!FFmpegNative.TryLoad(out var err))
        {
            Console.WriteLine("load failed: " + err?.Message);
            return 1;
        }

        foreach (string path in args)
        {
            Console.WriteLine($"==== {path}");
            if (!File.Exists(path)) { Console.WriteLine("  (not found)"); continue; }
            Console.WriteLine($"  size: {new FileInfo(path).Length:N0}");

            AVFormatContext* fmt = null;
            try
            {
                ffmpeg.avformat_open_input(&fmt, path, null, null);
                if (fmt == null) { Console.WriteLine("  open failed"); continue; }
                ffmpeg.avformat_find_stream_info(fmt, null);
                Console.WriteLine($"  format: {PtrStr(fmt->iformat->name)} / {PtrStr(fmt->iformat->long_name)}, streams={fmt->nb_streams}");
                for (uint i = 0; i < fmt->nb_streams; i++)
                {
                    AVStream* st = fmt->streams[i];
                    AVCodecParameters* p = st->codecpar;
                    Console.WriteLine($"  stream[{i}] type={p->codec_type} codec={ffmpeg.avcodec_get_name(p->codec_id)} " +
                        $"rate={p->sample_rate} ch={p->ch_layout.nb_channels} layout_order={p->ch_layout.order} " +
                        $"bits_raw={p->bits_per_raw_sample} bits_coded={p->bits_per_coded_sample} " +
                        $"block_align={p->block_align} tag=0x{p->codec_tag:X8}");
                }

                int si = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
                if (si < 0) { Console.WriteLine("  => NO AUDIO STREAM"); continue; }
                AVStream* stream = fmt->streams[si];
                AVCodec* dec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
                AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(dec);
                ffmpeg.avcodec_parameters_to_context(ctx, stream->codecpar);
                ctx->thread_count = 0;
                ffmpeg.avcodec_open2(ctx, dec, null);
                Console.WriteLine($"  decCtx: fmt={ctx->sample_fmt} rate={ctx->sample_rate} ch={ctx->ch_layout.nb_channels} order={ctx->ch_layout.order}");

                bool scan = args.Contains("--scan");
                var pkt = ffmpeg.av_packet_alloc();
                var frame = ffmpeg.av_frame_alloc();
                int shown = 0; long idx = 0;
                int lastFmt = -1, lastRate = -1, lastCh = -1;
                while (ffmpeg.av_read_frame(fmt, pkt) >= 0)
                {
                    if (pkt->stream_index != si) { ffmpeg.av_packet_unref(pkt); continue; }
                    ffmpeg.avcodec_send_packet(ctx, pkt);
                    ffmpeg.av_packet_unref(pkt);
                    while (ffmpeg.avcodec_receive_frame(ctx, frame) >= 0)
                    {
                        bool changed = lastFmt != -1 && (frame->format != lastFmt || frame->sample_rate != lastRate || frame->ch_layout.nb_channels != lastCh);
                        if (shown < 5 || changed)
                            Console.WriteLine($"  frame[{idx}]: fmt={(AVSampleFormat)frame->format} rate={frame->sample_rate} " +
                                $"ch={frame->ch_layout.nb_channels} order={frame->ch_layout.order} samples={frame->nb_samples}{(changed ? "  <<<< PARAM CHANGED" : "")}");
                        lastFmt = frame->format; lastRate = frame->sample_rate; lastCh = frame->ch_layout.nb_channels;
                        ffmpeg.av_frame_unref(frame);
                        shown++; idx++;
                        if (!scan && shown >= 5) break;
                    }
                    if (!scan && shown >= 5) break;
                }
                Console.WriteLine($"  total frames seen: {idx}");
                ffmpeg.av_frame_free(&frame);
                ffmpeg.av_packet_free(&pkt);
                ffmpeg.avcodec_free_context(&ctx);
            }
            finally
            {
                if (fmt != null) ffmpeg.avformat_close_input(&fmt);
            }
        }
        return 0;
    }

    private static string PtrStr(byte* p)
    {
        if (p == null) return "";
        int len = 0;
        while (p[len] != 0) len++;
        return System.Text.Encoding.ASCII.GetString(p, len);
    }
}
