using FancyToolAva.Models;
using FancyToolAva.Services;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging.Abstractions;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunInterpolationCases(string source, string workDir)
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        using var http = new HttpClient();
        var downloads = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
        var interpolation = new VideoInterpolationService(downloads, NullLogger<VideoInterpolationService>.Instance);
        var enhancement = new VideoSuperResolutionService(downloads, NullLogger<VideoSuperResolutionService>.Instance);
        var service = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance, enhancement, interpolation);
        interpolation.DeviceChanged += device => Console.WriteLine($"    interpolation device: {device.Name}; fallback: {device.FallbackReason ?? "none"}");
        RunExtra("DirectML fallback executes the complete RIFE graph on GPU", () => Task.Run(() =>
        {
            string path = Path.Combine(AppPaths.ModelsDirectory, "Interpolation", VideoInterpolationService.GetModel(VideoInterpolationModel.Rife425Lite).FileName);
            Console.WriteLine("    creating DirectML session on the conversion worker");
            using var session = WindowsMlAccelerator.CreateDirectMlSession(path, 160, 120, CancellationToken.None, out var device, true);
            Console.WriteLine("    DirectML session ready; executing RIFE");
            float[] input = new float[7 * 160 * 120]; Array.Fill(input, .5f);
            using var tensor = Microsoft.ML.OnnxRuntime.OrtValue.CreateTensorValueFromMemory(input, [1, 7, 120, 160]);
            using var run = new Microsoft.ML.OnnxRuntime.RunOptions();
            using var result = session.Run(run, new Dictionary<string, Microsoft.ML.OnnxRuntime.OrtValue> { [session.InputMetadata.Single().Key] = tensor }, [session.OutputMetadata.Single().Key]);
            var pixels = result[0].GetTensorDataAsSpan<float>();
            if (pixels.Length != 3 * 160 * 120 || Math.Abs(pixels[0] - .5f) > .03f)
                throw new InvalidOperationException("DirectML RIFE output is incorrect");
            Console.WriteLine("    fallback device: " + device.Name);
        }).GetAwaiter().GetResult());
        foreach (var mode in new[] { VideoInterpolationMode.MotionCompensation, VideoInterpolationMode.Rife })
        {
            RunExtra($"{mode}: 2x clock, audio, trim and tail", () =>
            {
                string output = Path.Combine(workDir, $"interpolation-{mode}.mp4");
                var preview = new LastPreview();
                service.TranscodeAsync(source, output, Options(mode, .4, 1.4), preview: preview).GetAwaiter().GetResult();
                AssertInterpolated(service, output, 20, 20, 1, true);
                if (preview.Last == null || preview.Last.PositionSeconds < 1.34 || preview.Last.PositionSeconds >= 1.4)
                    throw new InvalidOperationException("Processed preview missed the tail or escaped the clip");
            });
            RunExtra($"{mode}: single frame clip and fourfold rate", () =>
            {
                string output = Path.Combine(workDir, $"interpolation-single-{mode}.mp4");
                var options = Options(mode, .4, .5); options.InterpolationMultiplier = 4;
                service.TranscodeAsync(source, output, options).GetAwaiter().GetResult();
                AssertInterpolated(service, output, 40, 4, .1, true);
            });
            RunExtra($"{mode}: rational target rate without a second fps filter", () =>
            {
                string output = Path.Combine(workDir, $"interpolation-fractional-{mode}.mp4");
                var options = Options(mode, .4, .8);
                options.InterpolationMultiplier = 0;
                options.FpsValue = 59.94;
                options.FpsMode = FpsMode.Fixed;
                service.TranscodeAsync(source, output, options).GetAwaiter().GetResult();
                AssertInterpolated(service, output, 60000d / 1001, 24, .4, true);
            });
            RunExtra($"{mode}: non-frame-aligned trim keeps a zero-based clock", () =>
            {
                string output = Path.Combine(workDir, $"interpolation-unaligned-{mode}.mp4");
                var options = Options(mode, .353, .953); options.InterpolationMultiplier = 0; options.FpsValue = 30;
                service.TranscodeAsync(source, output, options).GetAwaiter().GetResult();
                AssertInterpolated(service, output, 30, 18, .6, true);
            });
        }
        RunExtra("Intel NPU and explicit CPU execute actual interpolation", () =>
        {
            foreach (var backend in new[] { VideoSrBackend.PreferNpu, VideoSrBackend.Cpu })
            {
                var devices = new List<VideoSrDevice>();
                interpolation.DeviceChanged += devices.Add;
                try
                {
                    var options = Options(VideoInterpolationMode.Rife, .4, .7); options.InterpolationBackend = backend;
                    string output = Path.Combine(workDir, $"interpolation-device-{backend}.mp4");
                    service.TranscodeAsync(source, output, options).GetAwaiter().GetResult();
                    AssertInterpolated(service, output, 20, 6, .3, true);
                    string expected = backend == VideoSrBackend.Cpu ? "CPU" : "NPU";
                    if (devices.Count == 0 || !devices[^1].Name.StartsWith(expected)) throw new InvalidOperationException("Interpolation did not execute on " + expected);
                }
                finally { interpolation.DeviceChanged -= devices.Add; }
            }
        });
        RunExtra("AI scene cut protection and actual generated frame", () =>
        {
            string cuts = Path.Combine(workDir, "interpolation-cuts.avi");
            WriteTestAvi(cuts, 160, 120, 10, 2);
            FillAviFrames(cuts, (pixels, frame) => pixels.Fill(frame < 10 ? (byte)0 : (byte)255));
            string output = Path.Combine(workDir, "interpolation-cuts.mp4");
            service.TranscodeAsync(cuts, output, Options(VideoInterpolationMode.Rife, 0, 2)).GetAwaiter().GetResult();
            var beforeCut = service.GetPreviewAsync(output, .95).GetAwaiter().GetResult()!;
            var afterCut = service.GetPreviewAsync(output, 1).GetAwaiter().GetResult()!;
            if (beforeCut.Pixels[0] > 10 || afterCut.Pixels[0] < 245) throw new InvalidOperationException("Scene cut was cross-interpolated");
            var options = Options(VideoInterpolationMode.Rife, 0, 2); options.InterpolationSceneDetection = false;
            service.TranscodeAsync(cuts, output, options).GetAwaiter().GetResult();
            var middle = service.GetPreviewAsync(output, .95).GetAwaiter().GetResult()!;
            if (middle.Pixels[0] < 20 || middle.Pixels[0] > 235) throw new InvalidOperationException("Model did not generate an intermediate frame");
        });
        RunExtra("AI plus super-resolution, crop and scaling", () =>
        {
            foreach (var mode in new[] { VideoInterpolationMode.Rife, VideoInterpolationMode.MotionCompensation })
            foreach (var order in Enum.GetValues<VideoFrameProcessingOrder>())
            {
            string output = Path.Combine(workDir, $"interpolation-upscaled-{mode}-{order}.mp4");
            var options = Options(mode, .4, .6); options.FrameProcessingOrder = order;
            options.SuperResolutionEnabled = true;
            options.SuperResolutionModel = SuperResolutionModel.RealEsrganGeneralX4V3;
            options.SuperResolutionBackend = VideoSrBackend.PreferNpu;
            options.SuperResolutionAllowCpuFallback = false;
            options.CropEnabled = true; options.CropLeft = options.CropRight = 4;
            options.ScaleMode = ScaleMode.Exact; options.ScaleWidth = 200; options.ScaleHeight = 150;
            service.TranscodeAsync(source, output, options).GetAwaiter().GetResult();
            AssertInterpolated(service, output, 20, 4, .2, true);
            var info = service.ProbeAsync(output).GetAwaiter().GetResult()!;
            if (info.Width != 200 || info.Height != 150) throw new InvalidOperationException("Combined pipeline applied geometry incorrectly");
            }
        });
        RunExtra("explicit downscaling precedes the AI size limit", () =>
        {
            string large = Path.Combine(workDir, "interpolation-large-source.avi");
            WriteTestAvi(large, 4096, 2160, 1, 2);
            string output = Path.Combine(workDir, "interpolation-downscaled.mp4");
            var options = Options(VideoInterpolationMode.Rife, 0, 2);
            options.ScaleMode = ScaleMode.Exact; options.ScaleWidth = 80; options.ScaleHeight = 64;
            service.TranscodeAsync(large, output, options).GetAwaiter().GetResult();
            AssertInterpolated(service, output, 2, 4, 2, true);
            var info = service.ProbeAsync(output).GetAwaiter().GetResult()!;
            if (info.Width != 80 || info.Height != 64) throw new InvalidOperationException("Pre-interpolation scale was lost");
        });
        RunExtra("AI two-pass restarts the interpolation clock", () =>
        {
            var options = Options(VideoInterpolationMode.Rife, .4, .8);
            options.TwoPassEnabled = true; options.RateControl = RateControlMode.Bitrate; options.VideoBitrateKbps = 400;
            string output = Path.Combine(workDir, "interpolation-two-pass.mp4");
            service.TranscodeAsync(source, output, options).GetAwaiter().GetResult();
            AssertInterpolated(service, output, 20, 8, .4, true);
        });
        RunExtra("variable timestamps use temporal distances, not frame indices", () =>
        {
            string compressed = Path.Combine(workDir, "vfr-baseline.mp4");
            service.TranscodeAsync(source, compressed, new VideoTranscodeOptions { Preset = PresetLevel.Ultrafast }).GetAwaiter().GetResult();
            string vfr = Path.Combine(workDir, "variable-source.mp4");
            WriteVariableSource(compressed, vfr);
            var times = ReadVideoTimes(vfr);
            if (Math.Abs((times[2] - times[1]) - (times[1] - times[0])) < .03)
                throw new InvalidOperationException("Variable-frame-rate fixture has constant timestamps");
            foreach (var mode in new[] { VideoInterpolationMode.Rife, VideoInterpolationMode.MotionCompensation })
            {
                var options = Options(mode, 0, 1); options.InterpolationMultiplier = 0; options.FpsValue = 30;
                string output = Path.Combine(workDir, $"vfr-{mode}.mp4");
                service.TranscodeAsync(vfr, output, options).GetAwaiter().GetResult();
                AssertInterpolated(service, output, 30, 30, 1, true);
            }
        });
        RunExtra("interpolated GIF uses the GIF target rate", () =>
        {
            string output = Path.Combine(workDir, "interpolated.gif");
            var options = Options(VideoInterpolationMode.Rife, .4, 1.4);
            options.Container = VideoContainer.Gif; options.VideoCodec = VideoCodec.Gif; options.AudioCodec = AudioCodec.None;
            options.GifFps = 20; options.GifWidth = 120;
            service.TranscodeAsync(source, output, options).GetAwaiter().GetResult();
            AssertInterpolated(service, output, 20, 20, 1, false);
        });
        RunExtra("AI cancellation leaves no partial output", () =>
        {
            using var ct = new CancellationTokenSource();
            string output = Path.Combine(workDir, "interpolation-canceled-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                service.TranscodeAsync(source, output, Options(VideoInterpolationMode.Rife, 0, 2), ct: ct.Token,
                    preview: new CancelPreview(ct)).GetAwaiter().GetResult();
                throw new InvalidOperationException("Interpolation ignored cancellation");
            }
            catch (OperationCanceledException) { }
            if (File.Exists(output) || Directory.GetFiles(workDir, "*" + Path.GetFileNameWithoutExtension(output) + "*.tmp*").Length != 0)
                throw new InvalidOperationException("Canceled interpolation retained an output file");
        });
        RunExtra("HDR requires explicit tone mapping before interpolation", () =>
        {
            string hdr = Path.Combine(workDir, "interpolation-hdr.mp4");
            service.TranscodeAsync(source, hdr, new VideoTranscodeOptions { HdrOutput = VideoHdrOutput.ForceHdr,
                TrimEndSeconds = .3, Preset = PresetLevel.Ultrafast }).GetAwaiter().GetResult();
            foreach (var mode in new[] { VideoInterpolationMode.Rife, VideoInterpolationMode.MotionCompensation })
            {
                string output = Path.Combine(workDir, $"interpolation-hdr-{mode}.mp4");
                var options = Options(mode, 0, .3);
                try { service.TranscodeAsync(hdr, output, options).GetAwaiter().GetResult(); throw new InvalidOperationException("HDR interpolation silently lost dynamic range"); }
                catch (NotSupportedException) { }
                options.HdrOutput = VideoHdrOutput.ForceSdr;
                service.TranscodeAsync(hdr, output, options).GetAwaiter().GetResult();
                if (service.ProbeAsync(output).GetAwaiter().GetResult()!.IsHdr) throw new InvalidOperationException("Tone-mapped interpolation retained HDR tags");
            }
        });
        RunExtra("invalid interpolation rate is rejected", () =>
        {
            foreach (double fps in new[] { double.NaN, 0, double.PositiveInfinity, 241 })
            {
                var options = Options(VideoInterpolationMode.Rife, 0, 1); options.InterpolationMultiplier = 0; options.FpsValue = fps;
                try { service.TranscodeAsync(source, Path.Combine(workDir, "invalid.mp4"), options).GetAwaiter().GetResult(); throw new InvalidOperationException("Invalid interpolation rate accepted"); }
                catch (ArgumentException) { }
            }
        });
        Console.WriteLine($"Interpolation smoke tests: {_runCount} cases, {_failures} failures");
        return _failures == 0 ? 0 : 1;

        static VideoTranscodeOptions Options(VideoInterpolationMode mode, double start, double end) => new()
        {
            InterpolationMode = mode, InterpolationBackend = VideoSrBackend.Gpu, InterpolationAllowCpuFallback = false,
            TrimStartSeconds = start, TrimEndSeconds = end, Preset = PresetLevel.Ultrafast
        };
    }

    private static void AssertInterpolated(VideoTranscodeService service, string path, double fps, int expectedFrames, double duration, bool audio)
    {
        var probe = service.ProbeAsync(path).GetAwaiter().GetResult()!;
        if (Math.Abs(probe.Fps - fps) > 0.001 || probe.HasAudio != audio || Math.Abs(probe.DurationMs - duration * 1000) > 70)
            throw new InvalidOperationException($"Unexpected interpolated metadata: {probe}");
        var timestamps = ReadVideoTimes(path);
        if (timestamps.Count != expectedFrames || timestamps.Where((t, i) => Math.Abs(t - i / fps) > .0001).Any())
            throw new InvalidOperationException($"Output clock mismatch: expected {expectedFrames} frames at {fps}, got {timestamps.Count}: {string.Join(",", timestamps.Take(6))}");
    }

    private static unsafe List<double> ReadVideoTimes(string path)
    {
        AVFormatContext* input = null; AVCodecContext* decoder = null;
        AVPacket* packet = ffmpeg.av_packet_alloc(); AVFrame* frame = ffmpeg.av_frame_alloc();
        var result = new List<double>();
        try
        {
            if (ffmpeg.avformat_open_input(&input, path, null, null) < 0) throw new InvalidOperationException("Cannot open interpolated output");
            ffmpeg.avformat_find_stream_info(input, null);
            int index = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            var stream = input->streams[index];
            decoder = ffmpeg.avcodec_alloc_context3(ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id));
            ffmpeg.avcodec_parameters_to_context(decoder, stream->codecpar); ffmpeg.avcodec_open2(decoder, decoder->codec, null);
            while (true)
            {
                int ret = ffmpeg.av_read_frame(input, packet);
                if (ret >= 0 && packet->stream_index != index) { ffmpeg.av_packet_unref(packet); continue; }
                ffmpeg.avcodec_send_packet(decoder, ret < 0 ? null : packet);
                ffmpeg.av_packet_unref(packet);
                while (ffmpeg.avcodec_receive_frame(decoder, frame) >= 0)
                { result.Add(frame->best_effort_timestamp * ffmpeg.av_q2d(stream->time_base)); ffmpeg.av_frame_unref(frame); }
                if (ret < 0) break;
            }
        }
        finally
        {
            ffmpeg.av_frame_free(&frame); ffmpeg.av_packet_free(&packet);
            if (decoder != null) ffmpeg.avcodec_free_context(&decoder);
            if (input != null) ffmpeg.avformat_close_input(&input);
        }
        return result;
    }

    private delegate void AviFrameWriter(Span<byte> pixels, int frame);

    private static unsafe void WriteVariableSource(string source, string path)
    {
        AVFormatContext* input = null; AVFormatContext* output = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            ffmpeg.avformat_open_input(&input, source, null, null); ffmpeg.avformat_find_stream_info(input, null);
            ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", path);
            for (uint i = 0; i < input->nb_streams; i++)
            {
                var dest = ffmpeg.avformat_new_stream(output, null);
                ffmpeg.avcodec_parameters_copy(dest->codecpar, input->streams[i]->codecpar);
                dest->time_base = input->streams[i]->time_base;
            }
            ffmpeg.avio_open(&output->pb, path, ffmpeg.AVIO_FLAG_WRITE); ffmpeg.avformat_write_header(output, null);
            int videoFrame = 0;
            while (ffmpeg.av_read_frame(input, packet) >= 0)
            {
                var src = input->streams[packet->stream_index]; var dest = output->streams[packet->stream_index];
                if (src->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    double time = videoFrame / 2 * .2 + (videoFrame % 2 == 0 ? 0 : .05);
                    double length = videoFrame % 2 == 0 ? .05 : .15;
                    packet->pts = packet->dts = (long)Math.Round(time / ffmpeg.av_q2d(dest->time_base));
                    packet->duration = (long)Math.Round(length / ffmpeg.av_q2d(dest->time_base));
                    videoFrame++;
                }
                else ffmpeg.av_packet_rescale_ts(packet, src->time_base, dest->time_base);
                packet->pos = -1;
                if (ffmpeg.av_interleaved_write_frame(output, packet) < 0) throw new InvalidOperationException("Cannot create VFR fixture");
                ffmpeg.av_packet_unref(packet);
            }
            ffmpeg.av_write_trailer(output);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            if (input != null) ffmpeg.avformat_close_input(&input);
            if (output != null) { if (output->pb != null) ffmpeg.avio_closep(&output->pb); ffmpeg.avformat_free_context(output); }
        }
    }

    private static void FillAviFrames(string path, AviFrameWriter writer)
    {
        byte[] avi = File.ReadAllBytes(path);
        int count = 0;
        for (int i = 0; i < avi.Length - 8; i++)
            if (avi[i] == '0' && avi[i + 1] == '0' && avi[i + 2] == 'd' && avi[i + 3] == 'b')
            {
                int size = BitConverter.ToInt32(avi, i + 4);
                if (size <= 0 || size > avi.Length - i - 8) continue;
                writer(avi.AsSpan(i + 8, size), count++); i += 7 + size;
            }
        if (count == 0) throw new InvalidOperationException("AVI fixture contains no video frames");
        File.WriteAllBytes(path, avi);
    }
}
