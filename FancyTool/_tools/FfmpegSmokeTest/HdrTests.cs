using FFmpeg.AutoGen;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static unsafe int RunHdrCapabilities()
    {
        foreach (string name in new[] { "zscale", "tonemap", "libplacebo", "lut3d", "geq", "setparams" })
            Console.WriteLine($"HDR filter {name}: {ffmpeg.avfilter_get_by_name(name) != null}");
        return 0;
    }

    private static int RunHdrCases(VideoTranscodeService service, string workDir)
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        string source = Path.Combine(workDir, "hdr-white-source.avi");
        WriteTestAvi(source, 96, 64, 5, 1, whitePatch: true);
        string hdr = Path.Combine(workDir, "sdr-to-hdr.mp4");
        string sdr = Path.Combine(workDir, "hdr-to-sdr.mp4");
        VideoTranscodeOptions Options(VideoHdrOutput mode, VideoCodec codec = VideoCodec.H265) => new()
        {
            HdrOutput = mode, VideoCodec = codec, AudioCodec = AudioCodec.None,
            Preset = PresetLevel.Ultrafast, Crf = 12
        };
        RunExtra("SDR to HDR: PQ, BT.2020, 10-bit and expanded 1000-nit highlights", () =>
        {
            service.TranscodeAsync(source, hdr, Options(VideoHdrOutput.ForceHdr)).GetAwaiter().GetResult();
            AssertHdr(service.ProbeAsync(hdr).GetAwaiter().GetResult()!);
            double nits = ReadPqWhite(hdr);
            if (nits is < 850 or > 1150) throw new InvalidOperationException($"Inverse tone mapping white = {nits:F2} nits, expected approximately 1000");
            Console.WriteLine($"  measured white: {nits:F2} nits");
        });
        RunExtra("HDR to SDR: real tone mapping and BT.709 metadata", () =>
        {
            service.TranscodeAsync(hdr, sdr, Options(VideoHdrOutput.ForceSdr)).GetAwaiter().GetResult();
            var info = service.ProbeAsync(sdr).GetAwaiter().GetResult()!;
            if (info.IsHdr || info.ColorPrimaries != "bt709" || info.ColorTransfer != "bt709") throw new InvalidOperationException($"Wrong SDR metadata: {info}");
            var preview = service.GetPreviewAsync(sdr, 0).GetAwaiter().GetResult()!;
            int offset = (8 * preview.Width + 8) * 4;
            if (preview.Pixels[offset] < 240 || preview.Pixels[offset + 1] < 240 || preview.Pixels[offset + 2] < 240)
                throw new InvalidOperationException("HDR highlights were relabeled instead of tone-mapped");
        });
        RunExtra("same as source retains HDR and ten-bit precision", () =>
        {
            string output = Path.Combine(workDir, "hdr-preserved.mp4");
            service.TranscodeAsync(hdr, output, Options(VideoHdrOutput.SameAsSource)).GetAwaiter().GetResult();
            AssertHdr(service.ProbeAsync(output).GetAwaiter().GetResult()!);
            if (Math.Abs(ReadPqWhite(output) - ReadPqWhite(hdr)) > 100) throw new InvalidOperationException("HDR passthrough changed luminance");
        });
        RunExtra("same as source keeps SDR", () =>
        {
            string output = Path.Combine(workDir, "sdr-preserved.mp4");
            service.TranscodeAsync(source, output, Options(VideoHdrOutput.SameAsSource)).GetAwaiter().GetResult();
            if (service.ProbeAsync(output).GetAwaiter().GetResult()!.IsHdr) throw new InvalidOperationException("SDR source became HDR");
        });
        RunExtra("HDR LUT uses custom model paths with Unicode and filter punctuation", () =>
        {
            string previous = AppPaths.ModelsDirectory;
            try
            {
                string modelPath = Path.Combine(workDir, "模型 HDR's [色彩], store");
                AppPaths.InitializeModelsDirectory(modelPath);
                string output = Path.Combine(workDir, "hdr-custom-cache.mp4");
                service.TranscodeAsync(source, output, Options(VideoHdrOutput.ForceHdr)).GetAwaiter().GetResult();
                AssertHdr(service.ProbeAsync(output).GetAwaiter().GetResult()!);
                if (!File.Exists(Path.Combine(modelPath, "ColorLuts", "bt2446a-1000nits-v1.cube")))
                    throw new InvalidOperationException("HDR LUT ignored the selected model root");
            }
            finally { AppPaths.InitializeModelsDirectory(previous); }
        });
        RunExtra("software H.264 uses High 10 for HDR", () =>
        {
            string output = Path.Combine(workDir, "hdr-x264.mp4");
            service.TranscodeAsync(source, output, Options(VideoHdrOutput.ForceHdr, VideoCodec.H264)).GetAwaiter().GetResult();
            AssertHdr(service.ProbeAsync(output).GetAwaiter().GetResult()!);
        });
        RunExtra("HDR encoder rejects eight-bit-only output", () =>
        {
            string output = Path.Combine(workDir, "hdr-incompatible.mp4");
            try
            {
                service.TranscodeAsync(source, output, Options(VideoHdrOutput.ForceHdr, VideoCodec.Mpeg4)).GetAwaiter().GetResult();
                throw new InvalidOperationException("Eight-bit encoder accepted HDR");
            }
            catch (InvalidOperationException ex) when (ex.Message == LocalizationRegistry.Get("VideoTranscode.Msg_HdrCodecUnsupported")) { }
            if (File.Exists(output)) throw new InvalidOperationException("Unsupported HDR left an output file");
        });
        RunExtra("HDR source previews are tone-mapped for the SDR UI", () =>
        {
            var preview = service.GetPreviewAsync(hdr, 0).GetAwaiter().GetResult()!;
            int offset = (8 * preview.Width + 8) * 4;
            if (preview.Pixels[offset] < 240) throw new InvalidOperationException("HDR preview highlights are not tone-mapped");
        });
        RunExtra("HDR GIF conversion maps luminance before palette generation", () =>
        {
            string output = Path.Combine(workDir, "hdr-to-gif.gif");
            service.TranscodeAsync(hdr, output, new VideoTranscodeOptions
            {
                Container = VideoContainer.Gif, VideoCodec = VideoCodec.Gif, HdrOutput = VideoHdrOutput.ForceSdr,
                AudioCodec = AudioCodec.None, GifWidth = 96, GifFps = 5
            }).GetAwaiter().GetResult();
            if (service.ProbeAsync(output).GetAwaiter().GetResult()!.IsHdr) throw new InvalidOperationException("GIF must be SDR");
        });
        RunExtra("HLG follows source, converts to PQ, and tone-maps to SDR", () =>
        {
            string hlg = Path.Combine(workDir, "hlg-source.mkv");
            WriteHlgFixture(hlg);
            string same = Path.Combine(workDir, "hlg-preserved.mp4");
            service.TranscodeAsync(hlg, same, Options(VideoHdrOutput.SameAsSource)).GetAwaiter().GetResult();
            var info = service.ProbeAsync(same).GetAwaiter().GetResult()!;
            if (!info.IsHdr || info.ColorTransfer != "arib-std-b67" || info.BitDepth < 10) throw new InvalidOperationException("HLG was not preserved");
            string pq = Path.Combine(workDir, "hlg-to-pq.mp4");
            service.TranscodeAsync(hlg, pq, Options(VideoHdrOutput.ForceHdr)).GetAwaiter().GetResult();
            AssertHdr(service.ProbeAsync(pq).GetAwaiter().GetResult()!);
            string sdrHlg = Path.Combine(workDir, "hlg-to-sdr.mp4");
            service.TranscodeAsync(hlg, sdrHlg, Options(VideoHdrOutput.ForceSdr)).GetAwaiter().GetResult();
            if (service.ProbeAsync(sdrHlg).GetAwaiter().GetResult()!.IsHdr) throw new InvalidOperationException("HLG to SDR failed");
        });
        RunExtra("HDR and AI super-resolution compose pre/post color mapping", () =>
        {
            var enhanced = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance, new HdrTestSr());
            foreach (var mode in new[] { VideoHdrOutput.SameAsSource, VideoHdrOutput.ForceSdr })
            {
                string output = Path.Combine(workDir, "hdr-sr-" + mode + ".mp4");
                var options = Options(mode);
                options.SuperResolutionEnabled = true;
                enhanced.TranscodeAsync(hdr, output, options).GetAwaiter().GetResult();
                var info = enhanced.ProbeAsync(output).GetAwaiter().GetResult()!;
                if (info.Width != 192 || info.Height != 128 || info.IsHdr != (mode == VideoHdrOutput.SameAsSource))
                    throw new InvalidOperationException("Super-resolution changed output dynamic range or dimensions");
            }
        });
        Console.WriteLine($"HDR smoke tests: {_runCount} cases, {_failures} failures");
        return _failures == 0 ? 0 : 1;
    }

    private static void AssertHdr(VideoProbeInfo info)
    {
        if (!info.IsHdr || info.BitDepth < 10 || info.ColorPrimaries != "bt2020" || info.ColorTransfer != "smpte2084"
            || info.ColorSpace != "bt2020nc" || info.ColorRange != "tv")
            throw new InvalidOperationException($"Wrong HDR output: depth={info.BitDepth}, {info.ColorPrimaries}/{info.ColorTransfer}/{info.ColorSpace}/{info.ColorRange}");
    }

    private static unsafe double ReadPqWhite(string path)
    {
        AVFormatContext* input = null;
        AVCodecContext* decoder = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            int ret = ffmpeg.avformat_open_input(&input, path, null, null);
            if (ret < 0) throw new InvalidOperationException("Cannot open luminance fixture");
            ffmpeg.avformat_find_stream_info(input, null);
            int index = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            var parameters = input->streams[index]->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(parameters->codec_id);
            decoder = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(decoder, parameters);
            if (ffmpeg.avcodec_open2(decoder, codec, null) < 0) throw new InvalidOperationException("Cannot decode luminance fixture");
            while (ffmpeg.av_read_frame(input, packet) >= 0)
            {
                if (packet->stream_index == index)
                {
                    ffmpeg.avcodec_send_packet(decoder, packet);
                    if (ffmpeg.avcodec_receive_frame(decoder, frame) >= 0)
                    {
                        if ((AVPixelFormat)frame->format != AVPixelFormat.AV_PIX_FMT_YUV420P10LE) throw new InvalidOperationException("Expected 10-bit planar luminance");
                        int code = *(ushort*)(frame->data[0] + 8 * frame->linesize[0] + 8 * 2);
                        double signal = Math.Clamp((code - 64) / 876.0, 0, 1);
                        double p = Math.Pow(signal, 1 / (2523.0 / 32));
                        return 10000 * Math.Pow(Math.Max(p - 3424.0 / 4096, 0) / (2413.0 / 128 - 2392.0 / 128 * p), 1 / (2610.0 / 16384));
                    }
                }
                ffmpeg.av_packet_unref(packet);
            }
            throw new InvalidOperationException("Luminance fixture did not decode");
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            if (decoder != null) ffmpeg.avcodec_free_context(&decoder);
            if (input != null) ffmpeg.avformat_close_input(&input);
        }
    }

    private static unsafe void WriteHlgFixture(string path)
    {
        AVFormatContext* output = null;
        AVCodecContext* encoder = null;
        AVFrame* frame = ffmpeg.av_frame_alloc();
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            ffmpeg.avformat_alloc_output_context2(&output, null, "matroska", path);
            var codec = ffmpeg.avcodec_find_encoder_by_name("libx265");
            encoder = ffmpeg.avcodec_alloc_context3(codec);
            encoder->width = 96; encoder->height = 64;
            encoder->time_base = new AVRational { num = 1, den = 5 };
            encoder->framerate = new AVRational { num = 5, den = 1 };
            encoder->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P10LE;
            encoder->color_primaries = AVColorPrimaries.AVCOL_PRI_BT2020;
            encoder->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;
            encoder->colorspace = AVColorSpace.AVCOL_SPC_BT2020_NCL;
            encoder->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            encoder->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            ffmpeg.av_opt_set(encoder->priv_data, "preset", "ultrafast", 0);
            ffmpeg.av_opt_set(encoder->priv_data, "tune", "zerolatency", 0);
            ffmpeg.av_opt_set(encoder->priv_data, "profile", "main10", 0);
            ffmpeg.av_opt_set(encoder->priv_data, "x265-params", "log-level=error", 0);
            if (ffmpeg.avcodec_open2(encoder, codec, null) < 0) throw new InvalidOperationException("Cannot create HLG fixture");
            var stream = ffmpeg.avformat_new_stream(output, null);
            stream->time_base = encoder->time_base;
            ffmpeg.avcodec_parameters_from_context(stream->codecpar, encoder);
            if (ffmpeg.avio_open(&output->pb, path, ffmpeg.AVIO_FLAG_WRITE) < 0 || ffmpeg.avformat_write_header(output, null) < 0)
                throw new InvalidOperationException("Cannot write HLG fixture");
            frame->width = 96; frame->height = 64; frame->format = (int)encoder->pix_fmt;
            frame->color_primaries = encoder->color_primaries; frame->color_trc = encoder->color_trc;
            frame->colorspace = encoder->colorspace; frame->color_range = encoder->color_range;
            if (ffmpeg.av_frame_get_buffer(frame, 32) < 0) throw new OutOfMemoryException();
            for (int i = 0; i < 5; i++)
            {
                ffmpeg.av_frame_make_writable(frame);
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 96; x++) ((ushort*)(frame->data[0] + y * frame->linesize[0]))[x] = (ushort)(64 + 876 * x / 95);
                for (uint plane = 1; plane <= 2; plane++)
                    for (int y = 0; y < 32; y++)
                        for (int x = 0; x < 48; x++) ((ushort*)(frame->data[plane] + y * frame->linesize[plane]))[x] = 512;
                frame->pts = i;
                if (ffmpeg.avcodec_send_frame(encoder, frame) < 0) throw new InvalidOperationException("Cannot encode HLG fixture");
                WriteFixturePackets(output, encoder, stream, packet);
            }
            ffmpeg.avcodec_send_frame(encoder, null);
            WriteFixturePackets(output, encoder, stream, packet);
            ffmpeg.av_write_trailer(output);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame); ffmpeg.av_packet_free(&packet);
            if (encoder != null) ffmpeg.avcodec_free_context(&encoder);
            if (output != null) { if (output->pb != null) ffmpeg.avio_closep(&output->pb); ffmpeg.avformat_free_context(output); }
        }
    }

    private static unsafe void WriteFixturePackets(AVFormatContext* output, AVCodecContext* encoder, AVStream* stream, AVPacket* packet)
    {
        while (ffmpeg.avcodec_receive_packet(encoder, packet) >= 0)
        {
            ffmpeg.av_packet_rescale_ts(packet, encoder->time_base, stream->time_base);
            packet->stream_index = stream->index;
            if (ffmpeg.av_interleaved_write_frame(output, packet) < 0) throw new InvalidOperationException("Cannot mux HLG fixture");
            ffmpeg.av_packet_unref(packet);
        }
    }

    // Isolate color-pipeline composition from neural inference, which is already
    // covered by SuperResolutionTests against the real Windows ML models.
    private sealed class HdrTestSr : IVideoSuperResolutionService
    {
        public event Action<ModelDownloadProgress>? DownloadProgressChanged { add { } remove { } }
        public event Action<VideoSrDevice>? DeviceChanged { add { } remove { } }
        public bool IsModelAvailable(SuperResolutionModel model) => true;
        public Task EnsureModelAsync(SuperResolutionModel model, CancellationToken ct = default) => Task.CompletedTask;
        public Task InstallAccelerationAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IVideoFrameUpscaler> CreateSessionAsync(VideoTranscodeOptions options, CancellationToken ct = default)
            => Task.FromResult<IVideoFrameUpscaler>(new HdrTestUpscaler());
    }

    private sealed class HdrTestUpscaler : IVideoFrameUpscaler
    {
        public VideoSrDevice Device => new("test", false);
        public SKBitmap Upscale(SKBitmap source, int scale, CancellationToken ct)
        {
            var output = new SKBitmap(new SKImageInfo(source.Width * scale, source.Height * scale, SKColorType.Bgra8888, SKAlphaType.Opaque));
            using var canvas = new SKCanvas(output);
            canvas.DrawBitmap(source, new SKRect(0, 0, output.Width, output.Height));
            return output;
        }
        public void Dispose() { }
    }
}
