using System.Globalization;
using System.Text.Json;
using FancyToolAva.Services;
using FFmpeg.AutoGen;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static unsafe int RunPipelineQuality(string workDir, string[] args)
    {
        string Arg(string key) => args[Array.IndexOf(args, key) + 1];
        var results = new List<object>();
        var json = new JsonSerializerOptions { WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        foreach (string mode in (args.Contains("--modes") ? Arg("--modes") : "sr,rife,both").Split(','))
        {
            string before = Path.Combine(Arg("--before"), mode + "-" + (args.Contains("--before-iteration") ? Arg("--before-iteration") : "3"), "output.mp4");
            string after = Path.Combine(Arg("--after"), mode + "-" + (args.Contains("--after-iteration") ? Arg("--after-iteration") : "3"), "output.mp4");
            string psnr = Path.Combine(workDir, mode + "-psnr.txt"), ssim = Path.Combine(workDir, mode + "-ssim.txt");
            string FilterPath(string path)
            {
                // 诊断入口仅接受当前工作区的相对路径，避免 movie 选项里的 Windows 冒号转义歧义。
                string relative = Path.GetRelativePath(Environment.CurrentDirectory, Path.GetFullPath(path)).Replace('\\', '/');
                if (relative.Contains(':') || relative.Contains('\'') || relative.Contains('[') || relative.Contains(';')) throw new ArgumentException("Unsupported diagnostic path");
                return relative;
            }
            string description = $"movie=filename='{FilterPath(before)}'[reference];movie=filename='{FilterPath(after)}'[candidate];"
                + $"[reference]split=2[rp][rs];[candidate][rp]psnr=stats_file='{FilterPath(psnr)}'[p];[p][rs]ssim=stats_file='{FilterPath(ssim)}'[out]";
            AVFilterGraph* graph = ffmpeg.avfilter_graph_alloc();
            AVFilterContext* sink = null;
            AVFilterInOut* inputs = null; AVFilterInOut* outputs = null;
            AVFrame* frame = ffmpeg.av_frame_alloc();
            int frames = 0;
            try
            {
                int ret = ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("buffersink"), "sink", null, null, graph);
                if (ret >= 0) ret = ffmpeg.avfilter_graph_parse_ptr(graph, description, &inputs, &outputs, null);
                if (ret < 0 || inputs != null || outputs == null || outputs->next != null) throw FFmpegNative.CreateException(ret, "Quality graph parse");
                ret = ffmpeg.avfilter_link(outputs->filter_ctx, (uint)outputs->pad_idx, sink, 0);
                if (ret >= 0) ret = ffmpeg.avfilter_graph_config(graph, null);
                if (ret < 0) throw FFmpegNative.CreateException(ret, "Quality graph config");
                while ((ret = ffmpeg.av_buffersink_get_frame(sink, frame)) >= 0)
                { frames++; ffmpeg.av_frame_unref(frame); }
                if (ret != ffmpeg.AVERROR_EOF) throw FFmpegNative.CreateException(ret, "Quality drain");
            }
            finally
            {
                ffmpeg.avfilter_inout_free(&inputs); ffmpeg.avfilter_inout_free(&outputs);
                ffmpeg.av_frame_free(&frame); ffmpeg.avfilter_graph_free(&graph);
            }
            double[] Field(string file, string key) => File.ReadLines(file).Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .First(part => part.StartsWith(key + ":", StringComparison.Ordinal))).Select(part => double.Parse(part[(key.Length + 1)..].Split('(')[0], CultureInfo.InvariantCulture)).ToArray();
            var mse = Field(psnr, "mse_avg"); var similarities = Field(ssim, "All");
            var beforeTimes = ReadVideoTimes(before); var afterTimes = ReadVideoTimes(after);
            bool clock = beforeTimes.SequenceEqual(afterTimes) && frames == beforeTimes.Count;
            var service = new VideoTranscodeService(Microsoft.Extensions.Logging.Abstractions.NullLogger<VideoTranscodeService>.Instance);
            var beforeProbe = service.ProbeAsync(before).GetAwaiter().GetResult()!;
            var afterProbe = service.ProbeAsync(after).GetAwaiter().GetResult()!;
            double maximum = (1 << beforeProbe.BitDepth) - 1;
            bool color = beforeProbe.BitDepth == afterProbe.BitDepth && beforeProbe.ColorTransfer == afterProbe.ColorTransfer
                && beforeProbe.ColorPrimaries == afterProbe.ColorPrimaries && beforeProbe.IsHdr == afterProbe.IsHdr;
            double psnrDb = 10 * Math.Log10(maximum * maximum / mse.Average());
            bool passed = clock && color && similarities.Min() >= .98 && psnrDb >= 40;
            var result = new { mode, frames, clock, color, beforeProbe.BitDepth, passed, psnrDb, ssim = similarities.Average(), minSsim = similarities.Min(), minPsnr = 10 * Math.Log10(maximum * maximum / mse.Max()) };
            Console.WriteLine(JsonSerializer.Serialize(result, json)); results.Add(result);
            File.WriteAllText(Path.Combine(workDir, "quality.json"), JsonSerializer.Serialize(results, json));
            if (!passed) throw new InvalidOperationException("CUDA quality regression: " + mode);
        }
        File.WriteAllText(Path.Combine(workDir, "quality.json"), JsonSerializer.Serialize(results, json));
        return 0;
    }
}
