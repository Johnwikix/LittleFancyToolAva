using System.Text.Json;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging.Abstractions;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunHdrCudaCases(string workDir)
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        RunExtra("LUT DMA completes before nonblocking HDR kernels", RunHdrLutUploadStress);
        using var http = new HttpClient();
        var downloads = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
        using var sr = new VideoSuperResolutionService(downloads, NullLogger<VideoSuperResolutionService>.Instance,
            new AppPreferences { SuperResolutionTileSizeIndex = 0 });
        var rife = new VideoInterpolationService(downloads, NullLogger<VideoInterpolationService>.Instance);
        using var lifetime = TestServiceLifetime(rife);
        var service = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance, sr, rife);
        var records = new List<object>();
        var raw = Path.Combine(workDir, "sdr-source.avi");
        WriteTestAvi(raw, 170, 146, 8, 1, whitePatch: true);
        var pq = Path.Combine(workDir, "pq-source.mp4");
        service.TranscodeAsync(raw, pq, Options(VideoHdrOutput.ForceHdr)).GetAwaiter().GetResult();
        var hlg = Path.Combine(workDir, "hlg-source.mkv");
        WriteHlgFixture(hlg, 170, 146, 8, 8);
        var ramp = Path.Combine(workDir, "pq-ramp-unaligned.mkv");
        WriteHlgFixture(ramp, 170, 146, 8, 8, pq: true);

        VideoTranscodeOptions Options(VideoHdrOutput output) => new()
        {
            HardwareBackend = HardwareBackend.Nvidia, VideoCodec = VideoCodec.H265,
            HdrOutput = output, Preset = PresetLevel.Fast, Crf = 12, AudioCodec = AudioCodec.Aac,
            SuperResolutionModel = VideoSrModel.FsrcnnxM, SuperResolutionUseFp16 = true,
            SuperResolutionScale = 2, InterpolationModel = VideoInterpolationModel.Rife425Lite,
            InterpolationMultiplier = 4, FrameProcessingOrder = VideoFrameProcessingOrder.InterpolationThenUpscale
        };

        long Counter(string directory, string phase) => long.Parse(File.ReadLines(Directory.GetFiles(directory, "pipeline-*.csv").OrderBy(path => File.GetLastWriteTimeUtc(path)).Last())
            .Single(line => line.StartsWith(phase + ",", StringComparison.Ordinal)).Split(',')[1]);

        void Check(string name, string input, VideoTranscodeOptions options, bool cuda = true)
        {
            RunExtra(name, () =>
            {
                string directory = Path.Combine(workDir, name.Replace(' ', '-'));
                Directory.CreateDirectory(directory);
                Environment.SetEnvironmentVariable("FANCYTOOL_VIDEO_PROFILE", directory);
                try
                {
                    string output = Path.Combine(directory, "output.mp4");
                    var source = service.ProbeAsync(input).GetAwaiter().GetResult()!;
                    int sourceFrames = ReadVideoTimes(input).Count;
                    bool hdr = options.HdrOutput == VideoHdrOutput.ForceHdr || options.HdrOutput == VideoHdrOutput.SameAsSource && source.IsHdr;
                    var preview = new LastPreview();
                    service.TranscodeAsync(input, output, options, preview: preview).GetAwaiter().GetResult();
                    var info = service.ProbeAsync(output).GetAwaiter().GetResult()!;
                    var times = ReadVideoTimes(output);
                    int count = sourceFrames * (options.InterpolationMode == VideoInterpolationMode.None ? 1 : options.InterpolationMultiplier);
                    int scale = options.SuperResolutionEnabled ? options.SuperResolutionScale : 1;
                    string transfer = hdr ? options.HdrOutput == VideoHdrOutput.ForceHdr ? "smpte2084" : source.ColorTransfer : "bt709";
                    if (info.IsHdr != hdr || hdr && info.BitDepth != 10 || info.ColorTransfer != transfer
                        || info.ColorPrimaries != (hdr ? "bt2020" : "bt709") || times.Count != count
                        || info.Width != (source.Width - (options.CropEnabled ? options.CropLeft + options.CropRight : 0)) * scale
                        || info.Height != (source.Height - (options.CropEnabled ? options.CropTop + options.CropBottom : 0)) * scale || info.HasAudio != source.HasAudio
                        || times.Zip(times.Skip(1)).Any(t => t.Second <= t.First) || preview.Last == null)
                        throw new InvalidOperationException($"HDR contract changed: {info}; frames={times.Count}, preview={preview.Last != null}");
                    long decoded = Counter(directory, "CudaDecode"), downloaded = Counter(directory, "DecodeDownload");
                    if (cuda && (decoded != sourceFrames || downloaded != 0))
                        throw new InvalidOperationException($"GPU residency failed: decoded={decoded}, downloaded={downloaded}");
                    if (hdr && options.InterpolationMode == VideoInterpolationMode.None && !options.SuperResolutionEnabled && source.ColorTransfer == "smpte2084")
                    {
                        double before = ReadPqWhite(input), after = ReadPqWhite(output);
                        if (Math.Abs(before - after) > Math.Max(10, before * .03)) throw new InvalidOperationException($"PQ luminance changed: {before} -> {after}");
                    }
                    var evidence = ReadHdrEvidence(output);
                    if (!hdr && (evidence.MaxCLL != null || evidence.MasteringMaxNits != null))
                        throw new InvalidOperationException("SDR output retained HDR static metadata");
                    bool generated = hdr && (!source.IsHdr || options.SuperResolutionEnabled || options.InterpolationMode != VideoInterpolationMode.None);
                    if (generated && (evidence.MaxCLL != 1000 || evidence.MasteringMaxNits != 1000))
                        throw new InvalidOperationException($"Generated HDR metadata changed: {evidence}");
                    if (hdr && name.Contains("wide") && evidence.LumaLevels <= 256)
                        throw new InvalidOperationException($"Ten-bit path collapsed to eight bits: {evidence}");
                    records.Add(new { name, info.BitDepth, info.ColorTransfer, frames = times.Count, decoded, downloaded, evidence,
                        previewWidth = preview.Last.Width, previewHeight = preview.Last.Height });
                }
                finally { Environment.SetEnvironmentVariable("FANCYTOOL_VIDEO_PROFILE", null); }
            });
        }

        foreach (var input in new[] { pq, hlg, ramp })
        foreach (var output in Enum.GetValues<VideoHdrOutput>())
        foreach (string mode in new[] { "plain", "sr", "rife", "both" })
        {
            var options = Options(output);
            options.SuperResolutionEnabled = mode is "sr" or "both";
            options.InterpolationMode = mode is "rife" or "both" ? VideoInterpolationMode.Rife : VideoInterpolationMode.None;
            Check($"{Path.GetFileNameWithoutExtension(input)} {output} {mode}", input, options);
        }

        foreach (int multiplier in new[] { 2, 6 })
        {
            var options = Options(VideoHdrOutput.SameAsSource);
            options.SuperResolutionEnabled = true;
            options.InterpolationMode = VideoInterpolationMode.Rife;
            options.InterpolationMultiplier = multiplier;
            options.FrameProcessingOrder = VideoFrameProcessingOrder.UpscaleThenInterpolation;
            Check($"PQ SR-first {multiplier}x", pq, options);
        }
        foreach (var model in VideoSrModelCatalog.Models)
        {
            var modelOptions = Options(VideoHdrOutput.SameAsSource);
            modelOptions.SuperResolutionEnabled = true;
            modelOptions.SuperResolutionModel = model.Model;
            modelOptions.SuperResolutionUseFp16 = model.SupportsFp16;
            modelOptions.InterpolationMode = VideoInterpolationMode.Rife;
            Check($"PQ {model.Model} and RIFE", pq, modelOptions, cuda: !model.RequiresDirectMl);
        }
        var four = Options(VideoHdrOutput.SameAsSource);
        four.SuperResolutionEnabled = true;
        four.SuperResolutionScale = 4;
        Check("PQ GPU SR 4x", pq, four);
        var cropped = Options(VideoHdrOutput.SameAsSource);
        cropped.SuperResolutionEnabled = true;
        cropped.InterpolationMode = VideoInterpolationMode.Rife;
        cropped.CropEnabled = true;
        cropped.CropLeft = cropped.CropRight = 2;
        Check("PQ cropped mixed HDR RIFE SR", pq, cropped, cuda: false);
        var full = Options(VideoHdrOutput.SameAsSource);
        full.InterpolationMode = VideoInterpolationMode.Rife;
        full.InterpolationModel = VideoInterpolationModel.Rife425;
        Check("PQ RIFE Full", pq, full);
        var av1 = Options(VideoHdrOutput.SameAsSource);
        av1.VideoCodec = VideoCodec.Av1Svt;
        av1.SuperResolutionEnabled = true;
        av1.InterpolationMode = VideoInterpolationMode.Rife;
        Check("PQ AV1 NVENC 10bit", pq, av1);

        var small = Path.Combine(workDir, "hlg-below-nvdec-minimum.mkv");
        WriteHlgFixture(small);
        var smallOptions = Options(VideoHdrOutput.SameAsSource);
        smallOptions.InterpolationMode = VideoInterpolationMode.Rife;
        smallOptions.SuperResolutionEnabled = true;
        smallOptions.SuperResolutionScale = 4;
        Check("HLG hardware-rejected size full software retry", small, smallOptions, cuda: false);

        var wide = Path.Combine(workDir, "pq-wide-ramp.mkv");
        WriteHlgFixture(wide, 1024, 146, 4, 4, pq: true);
        Check("PQ wide ten-bit ramp", wide, Options(VideoHdrOutput.SameAsSource));
        WriteHlgFixture(wide, 1024, 146, 4, 4);
        Check("HLG wide GPU conversion to PQ", wide, Options(VideoHdrOutput.ForceHdr));

        var previousCuda = Environment.GetEnvironmentVariable("FANCYTOOL_VIDEO_CUDA");
        var previousNvdec = Environment.GetEnvironmentVariable("FANCYTOOL_VIDEO_NVDEC");
        try
        {
            Environment.SetEnvironmentVariable("FANCYTOOL_VIDEO_CUDA", "0");
            var mixed = Options(VideoHdrOutput.SameAsSource);
            mixed.InterpolationMode = VideoInterpolationMode.Rife;
            Check("PQ mixed NVDEC CPU-color RIFE HDR", pq, mixed, cuda: false);
            Environment.SetEnvironmentVariable("FANCYTOOL_VIDEO_NVDEC", "0");
            Check("PQ software-decode CPU-color RIFE HDR", pq, mixed, cuda: false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FANCYTOOL_VIDEO_CUDA", previousCuda);
            Environment.SetEnvironmentVariable("FANCYTOOL_VIDEO_NVDEC", previousNvdec);
        }
        RunExtra("PQ GPU cancellation and cached-session retry", () =>
        {
            var options = Options(VideoHdrOutput.SameAsSource);
            options.SuperResolutionEnabled = true;
            options.InterpolationMode = VideoInterpolationMode.Rife;
            string output = Path.Combine(workDir, "cancel.mp4");
            using var stop = new CancellationTokenSource();
            try
            {
                service.TranscodeAsync(pq, output, options, ct: stop.Token, preview: new CancelPreview(stop)).GetAwaiter().GetResult();
                throw new InvalidOperationException("HDR cancellation ignored");
            }
            catch (OperationCanceledException) { }
            if (File.Exists(output)) throw new InvalidOperationException("HDR cancellation left output");
            Check("PQ retry after cancellation", pq, options);
        });
        var sdrHdr = Options(VideoHdrOutput.ForceHdr);
        sdrHdr.SuperResolutionEnabled = true;
        sdrHdr.InterpolationMode = VideoInterpolationMode.Rife;
        var compressedSdr = Path.Combine(workDir, "sdr-source.mp4");
        var sdrOptions = Options(VideoHdrOutput.ForceSdr);
        sdrOptions.VideoCodec = VideoCodec.H264;
        sdrOptions.Profile = "high";
        service.TranscodeAsync(raw, compressedSdr, sdrOptions).GetAwaiter().GetResult();
        Check("SDR to HDR SR RIFE NVENC", compressedSdr, sdrHdr);
        File.WriteAllText(Path.Combine(workDir, "hdr-cuda.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"HDR CUDA: {_runCount} cases, {_failures} failures");
        return _failures == 0 ? 0 : 1;
    }
}
