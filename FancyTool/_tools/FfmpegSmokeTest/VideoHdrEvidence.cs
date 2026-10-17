using FancyToolAva.Services;
using FFmpeg.AutoGen;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private sealed record HdrFrameEvidence(int LumaLevels, uint? MaxCLL, double? MasteringMaxNits);

    private static void RunHdrLutUploadStress()
    {
#if FANCYTOOL_PRE_HDR_BASELINE
        throw new NotSupportedException("HDR GPU validation requires the HDR implementation");
#else
        const int width = 1920, height = 1080, size = 65;
        using var cuda = new CudaVideoContext(0);
        var input = new uint[width * height];
        for (int i = 0; i < input.Length; i++) input[i] = 0xff000000u | (unchecked((uint)i * 1664525u + 1013904223u) & 0xffffffu);
        using var source = cuda.UploadLut(System.Runtime.InteropServices.MemoryMarshal.Cast<uint, float>(input));
        using var output = cuda.Allocate(width, height);
        var values = new float[size * size * size * 4];
        for (int iteration = 0; iteration < 12; iteration++)
        {
            bool invert = (iteration & 1) != 0;
            for (int b = 0; b < size; b++)
                for (int g = 0; g < size; g++)
                    for (int r = 0; r < size; r++)
                    {
                        int index = ((b * size + g) * size + r) * 4;
                        values[index] = (invert ? size - 1 - r : r) / (float)(size - 1);
                        values[index + 1] = g / (float)(size - 1);
                        values[index + 2] = b / (float)(size - 1);
                    }
            // 模块首次执行后已暖机；交替 LUT 内容，让未完成的上传不能被旧内容掩盖。
            using var lut = cuda.UploadLut(values);
            cuda.HdrBgra(source.Frame.Data, 0, width * 4, 0, 0, 1, true, lut.Frame.Data, size, output.Frame);
            using var actual = Download(cuda, output.Frame);
            var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(actual.GetPixelSpan());
            for (int i = 0; i < input.Length; i++)
            {
                uint expected = invert ? input[i] ^ 0x00ff0000u : input[i];
                if (pixels[i] != expected) throw new InvalidOperationException($"Incomplete LUT upload at iteration {iteration}, pixel {i}");
            }
        }
#endif
    }

    private static unsafe HdrFrameEvidence ReadHdrEvidence(string path)
    {
        AVFormatContext* input = null;
        AVCodecContext* decoder = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            if (packet == null || frame == null) throw new OutOfMemoryException();
            int result = ffmpeg.avformat_open_input(&input, path, null, null);
            if (result >= 0) result = ffmpeg.avformat_find_stream_info(input, null);
            int index = result < 0 ? result : ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (index < 0) throw FFmpegNative.CreateException(index, "HDR evidence input");
            var parameters = input->streams[index]->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(parameters->codec_id);
            decoder = ffmpeg.avcodec_alloc_context3(codec);
            if (decoder == null) throw new OutOfMemoryException();
            result = ffmpeg.avcodec_parameters_to_context(decoder, parameters);
            if (result >= 0) result = ffmpeg.avcodec_open2(decoder, codec, null);
            if (result < 0) throw FFmpegNative.CreateException(result, "HDR evidence decoder");
            bool eof = false;
            while (true)
            {
                result = ffmpeg.avcodec_receive_frame(decoder, frame);
                if (result >= 0) break;
                if (eof) throw FFmpegNative.CreateException(result, "HDR evidence first frame");
                do { ffmpeg.av_packet_unref(packet); result = ffmpeg.av_read_frame(input, packet); }
                while (result >= 0 && packet->stream_index != index);
                eof = result < 0;
                result = ffmpeg.avcodec_send_packet(decoder, eof ? null : packet);
                if (result < 0) throw FFmpegNative.CreateException(result, "HDR evidence packet");
            }
            var levels = new HashSet<ushort>();
            if ((AVPixelFormat)frame->format == AVPixelFormat.AV_PIX_FMT_YUV420P10LE)
                for (int y = 0; y < frame->height; y++)
                    for (int x = 0; x < frame->width; x++) levels.Add(((ushort*)(frame->data[0] + y * frame->linesize[0]))[x]);
            var light = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
            var master = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
            return new(levels.Count, light == null ? null : ((AVContentLightMetadata*)light->data)->MaxCLL,
                master == null ? null : ffmpeg.av_q2d(((AVMasteringDisplayMetadata*)master->data)->max_luminance));
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            if (decoder != null) ffmpeg.avcodec_free_context(&decoder);
            if (input != null) ffmpeg.avformat_close_input(&input);
        }
    }
}
