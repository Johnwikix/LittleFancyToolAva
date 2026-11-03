using FancyToolAva.Models;
using FFmpeg.AutoGen;
using System.Text;

namespace FancyToolAva.Services;

public sealed unsafe partial class VideoTranscodeService
{
    private readonly record struct VideoColor(AVColorPrimaries Primaries, AVColorTransferCharacteristic Transfer,
        AVColorSpace Matrix, AVColorRange Range)
    {
        public bool IsHdr => Transfer is AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 or AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;
        public string RangeName => Range == AVColorRange.AVCOL_RANGE_JPEG ? "full" : "limited";
        public string InputParameters => $"pin={(int)Primaries}:tin={(int)Transfer}:min={(int)Matrix}:rin={RangeName}";
        public string OutputParameters => $"p={(int)Primaries}:t={(int)Transfer}:m={(int)Matrix}:r={RangeName}";
        public string FrameParameters => $"setparams=color_primaries={(int)Primaries}:color_trc={(int)Transfer}:colorspace={(int)Matrix}:range={RangeName}";
        public static VideoColor Sdr => new(AVColorPrimaries.AVCOL_PRI_BT709, AVColorTransferCharacteristic.AVCOL_TRC_BT709,
            AVColorSpace.AVCOL_SPC_BT709, AVColorRange.AVCOL_RANGE_MPEG);
        public static VideoColor SdrRgb => Sdr with { Matrix = AVColorSpace.AVCOL_SPC_RGB, Range = AVColorRange.AVCOL_RANGE_JPEG };
        public static VideoColor Hdr10 => new(AVColorPrimaries.AVCOL_PRI_BT2020, AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084,
            AVColorSpace.AVCOL_SPC_BT2020_NCL, AVColorRange.AVCOL_RANGE_MPEG);
    }

    private static VideoColor ReadColor(AVCodecContext* decoder)
        => ReadColor(decoder->pix_fmt, decoder->color_primaries, decoder->color_trc, decoder->colorspace, decoder->color_range);

    private static VideoColor ReadColor(AVFrame* frame)
        => ReadColor((AVPixelFormat)frame->format, frame->color_primaries, frame->color_trc, frame->colorspace, frame->color_range);

    private static VideoColor ReadColor(AVPixelFormat format, AVColorPrimaries primaries,
        AVColorTransferCharacteristic transfer, AVColorSpace matrix, AVColorRange range)
    {
        bool hdr = transfer is AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 or AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;
        var descriptor = ffmpeg.av_pix_fmt_desc_get(format);
        bool rgb = descriptor != null && (descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0;
        return new VideoColor(
            primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED
                ? hdr ? AVColorPrimaries.AVCOL_PRI_BT2020 : AVColorPrimaries.AVCOL_PRI_BT709 : primaries,
            transfer == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED ? AVColorTransferCharacteristic.AVCOL_TRC_BT709 : transfer,
            rgb ? AVColorSpace.AVCOL_SPC_RGB : matrix == AVColorSpace.AVCOL_SPC_UNSPECIFIED
                ? hdr ? AVColorSpace.AVCOL_SPC_BT2020_NCL : AVColorSpace.AVCOL_SPC_BT709 : matrix,
            range == AVColorRange.AVCOL_RANGE_UNSPECIFIED
                ? rgb ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG : range);
    }

    private static VideoColor ResolveOutputColor(VideoTranscodeOptions options, VideoColor source)
    {
        if (options.Container == VideoContainer.Gif || options.HdrOutput == VideoHdrOutput.ForceSdr) return VideoColor.Sdr;
        if (options.HdrOutput == VideoHdrOutput.ForceHdr) return VideoColor.Hdr10;
        return source with
        {
            Matrix = source.Matrix == AVColorSpace.AVCOL_SPC_RGB
                ? source.Primaries == AVColorPrimaries.AVCOL_PRI_BT2020 ? AVColorSpace.AVCOL_SPC_BT2020_NCL : AVColorSpace.AVCOL_SPC_BT709
                : source.Matrix,
            Range = AVColorRange.AVCOL_RANGE_MPEG
        };
    }

    private static string BuildColorFilters(VideoColor source, VideoColor target)
    {
        var filters = new List<string> { source.FrameParameters };
        if (source.IsHdr && !target.IsHdr)
        {
            // Float linear light avoids clipping HDR before the tone mapper. The
            // tonemap filter uses source mastering metadata for its peak estimate.
            filters.Add($"zscale={source.InputParameters}:p={(int)target.Primaries}:t=linear:m=gbr:r=full:npl=100");
            filters.Add("format=gbrpf32le");
            filters.Add("tonemap=tonemap=mobius:param=0.3:desat=2");
            filters.Add($"zscale=pin={(int)target.Primaries}:tin=linear:min=gbr:rin=full:{target.OutputParameters}:npl=100:d=error_diffusion");
        }
        else if (!source.IsHdr && target.IsHdr)
        {
            string path = EscapeFilterValue(HdrInverseToneMapping.GetLutPath().Replace('\\', '/'));
            // The LUT receives nonlinear full-range BT.2020 SDR and emits linear
            // HDR RGB normalized to 1000 nits, before PQ/HLG encoding and dithering.
            filters.Add($"zscale={source.InputParameters}:p=bt2020:t=bt709:m=gbr:r=full");
            filters.Add("format=gbrpf32le");
            filters.Add($"lut3d=file={path}:interp=tetrahedral");
            filters.Add($"zscale=pin=bt2020:tin=linear:min=gbr:rin=full:{target.OutputParameters}:npl=1000:d=error_diffusion");
        }
        else if (source != target)
        {
            filters.Add($"zscale={source.InputParameters}:{target.OutputParameters}:npl=1000:d=error_diffusion");
        }
        // zscale only supports planar RGB. A packed BGRA sink alone may negotiate
        // a YUV intermediate, which is incompatible with the RGB output matrix.
        if (target.Matrix == AVColorSpace.AVCOL_SPC_RGB && source != target)
            filters.Add("format=gbrpf32le");
        filters.Add(target.FrameParameters);
        return string.Join(',', filters);
    }

    private static string EscapeFilterValue(string value)
    {
        // A value is parsed twice: first by the graph parser, then by AVOptions.
        // Escape the inner option syntax before escaping the graph delimiters.
        var option = new StringBuilder(value.Length * 2);
        foreach (char c in value)
        {
            if (c is '\\' or '\'' or ':') option.Append('\\');
            option.Append(c);
        }
        var graph = new StringBuilder(option.Length * 2);
        foreach (char c in option.ToString())
        {
            if (c is '\\' or '\'' or '[' or ']' or ',' or ';') graph.Append('\\');
            graph.Append(c);
        }
        return graph.ToString();
    }

    private static void SetEncoderColor(AVCodecContext* encoder, AVCodecParameters* source, VideoColor color, bool generatedHdr)
    {
        encoder->color_primaries = color.Primaries;
        encoder->color_trc = color.Transfer;
        encoder->colorspace = color.Matrix;
        encoder->color_range = color.Range;
        if (!color.IsHdr) return;
        if (!generatedHdr)
        {
            CopyColorSideData(encoder, source, AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA);
            CopyColorSideData(encoder, source, AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL);
            return;
        }
        var side = ffmpeg.av_packet_side_data_new(&encoder->coded_side_data, &encoder->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA, (ulong)sizeof(AVMasteringDisplayMetadata), 0);
        if (side == null) throw new OutOfMemoryException();
        var master = (AVMasteringDisplayMetadata*)side->data;
        int[] x = [7080, 1700, 1310];
        int[] y = [2920, 7970, 460];
        for (uint i = 0; i < 3; i++)
        {
            var primary = new AVRational_array2();
            primary[0] = new AVRational { num = x[i], den = 10000 };
            primary[1] = new AVRational { num = y[i], den = 10000 };
            master->display_primaries[i] = primary;
        }
        master->white_point[0] = new AVRational { num = 3127, den = 10000 };
        master->white_point[1] = new AVRational { num = 3290, den = 10000 };
        master->min_luminance = new AVRational { num = 0, den = 1 };
        master->max_luminance = new AVRational { num = 1000, den = 1 };
        master->has_luminance = master->has_primaries = 1;
        side = ffmpeg.av_packet_side_data_new(&encoder->coded_side_data, &encoder->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL, (ulong)sizeof(AVContentLightMetadata), 0);
        if (side == null) throw new OutOfMemoryException();
        var light = (AVContentLightMetadata*)side->data;
        light->MaxCLL = 1000;
        light->MaxFALL = 0; // Unknown; do not invent a measured frame-average luminance.
    }

    private static void CopyColorSideData(AVCodecContext* encoder, AVCodecParameters* source, AVPacketSideDataType type)
    {
        var sourceSide = ffmpeg.av_packet_side_data_get(source->coded_side_data, source->nb_coded_side_data, type);
        if (sourceSide == null) return;
        var targetSide = ffmpeg.av_packet_side_data_new(&encoder->coded_side_data, &encoder->nb_coded_side_data, type, sourceSide->size, 0);
        if (targetSide == null) throw new OutOfMemoryException();
        Buffer.MemoryCopy(sourceSide->data, targetSide->data, (long)sourceSide->size, (long)sourceSide->size);
    }

    private static void SetFrameColor(AVFrame* frame, AVCodecContext* encoder)
    {
        frame->color_primaries = encoder->color_primaries;
        frame->color_trc = encoder->color_trc;
        frame->colorspace = encoder->colorspace;
        frame->color_range = encoder->color_range;
        // Dynamic metadata describes original pixels and cannot survive processing.
        ffmpeg.av_frame_remove_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS);
        ffmpeg.av_frame_remove_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_RPU_BUFFER);
        ffmpeg.av_frame_remove_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA);
        if (encoder->color_trc is not (AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 or AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67))
        {
            ffmpeg.av_frame_remove_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
            ffmpeg.av_frame_remove_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
        }
    }
}
