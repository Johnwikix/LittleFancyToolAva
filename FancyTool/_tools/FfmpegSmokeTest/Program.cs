using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FfmpegSmokeTest;

/// <summary>
/// 进程内 FFmpeg 转码全矩阵烟测：按 UI 实际允许的 容器×视频编码×音频编码 组合
/// （MP4 5×5、MKV 7×7、MOV 4×4、AVI 2×4、GIF）逐一转码并用进程内探测自校验，
/// 另覆盖 10bit/422 透传、两通、滤镜链、profile/level 与硬件编码尝试。
/// </summary>
internal static class Program
{
    private static int _runCount;
    private static int _failures;
    private static int _skips;
    private static readonly List<string> _failedNames = [];

    private static int Main(string[] args)
    {
        string workDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "fancytool-smoke");
        string sample = Path.Combine(workDir, "sample.mp4");
        string sample10bit = Path.Combine(workDir, "sample10bit.mkv");
        string sample422 = Path.Combine(workDir, "sample422.mkv");
        Directory.CreateDirectory(workDir);
        Console.WriteLine($"work dir: {workDir}");

        // 1. 库加载 + 关键编码器存在性
        var svc = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance);
        var ffmpegSvc = new FfmpegService(NullLogger<FfmpegService>.Instance);
        if (!ffmpegSvc.ValidateAsync().GetAwaiter().GetResult())
        {
            Console.WriteLine($"FATAL load: {ffmpegSvc.LastError}");
            return 1;
        }
        Console.WriteLine($"load OK: {ffmpegSvc.VersionInfo} v={ffmpegSvc.AvailableVideoEncoders.Count} a={ffmpegSvc.AvailableAudioEncoders.Count}");

        // 2. 全矩阵（与 ViewModel FilterVideoCodecsForContainer/FilterAudioCodecsForContainer 一致）
        (VideoContainer C, VideoCodec[] V, AudioCodec[] A)[] matrix =
        [
            (VideoContainer.Mp4,
                [VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1Aom, VideoCodec.Av1Svt, VideoCodec.Mpeg4],
                [AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Ac3, AudioCodec.Opus, AudioCodec.None]),
            (VideoContainer.Mkv,
                [VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1Aom, VideoCodec.Av1Svt, VideoCodec.Vp8, VideoCodec.Vp9, VideoCodec.Mpeg4],
                [AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Opus, AudioCodec.Vorbis, AudioCodec.Flac, AudioCodec.Ac3, AudioCodec.None]),
            (VideoContainer.Mov,
                [VideoCodec.H264, VideoCodec.H265, VideoCodec.Mpeg4],
                [AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Ac3, AudioCodec.None]),
            (VideoContainer.Avi,
                [VideoCodec.H264, VideoCodec.Mpeg4],
                [AudioCodec.Mp3, AudioCodec.Aac, AudioCodec.Ac3, AudioCodec.None]),
        ];

        int total = matrix.Sum(m => m.V.Length * m.A.Length) + 1 /*gif*/;
        Console.WriteLine($"matrix total: {total}");

        foreach (var (container, vcodecs, acodecs) in matrix)
        {
            foreach (VideoCodec v in vcodecs)
            {
                foreach (AudioCodec a in acodecs)
                {
                    RunCase(svc, $"{container}|{v}|{a}", sample, container, v, a, o =>
                    {
                        o.RateControl = RateControlMode.Crf;
                        o.Crf = 32;
                        o.Preset = PresetLevel.Ultrafast;
                        o.AudioBitrateKbps = 64;
                        if (v == VideoCodec.Av1Aom) o.Crf = 45; // aom 档位不同，取可比拟大小
                    });
                }
            }
        }
        RunCase(svc, "GIF|gif|None", sample, VideoContainer.Gif, VideoCodec.Gif, AudioCodec.None, o =>
        {
            o.GifWidth = 120; o.GifFps = 8; o.GifMaxColors = 64;
        });

        // 3. 特殊场景
        RunExtra("2pass-x264", () => svc.TranscodeAsync(sample, Out("x-2pass-264.mp4"), new VideoTranscodeOptions
        {
            Container = VideoContainer.Mp4, VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.Aac,
            RateControl = RateControlMode.Bitrate, VideoBitrateKbps = 500, TwoPassEnabled = true, Preset = PresetLevel.Ultrafast,
        }).GetAwaiter().GetResult());

        RunExtra("2pass-x265", () => svc.TranscodeAsync(sample, Out("x-2pass-265.mkv"), new VideoTranscodeOptions
        {
            Container = VideoContainer.Mkv, VideoCodec = VideoCodec.H265, AudioCodec = AudioCodec.None,
            RateControl = RateControlMode.Bitrate, VideoBitrateKbps = 400, TwoPassEnabled = true, Preset = PresetLevel.Ultrafast,
        }).GetAwaiter().GetResult());

        // 10bit / 422 源透传（输出给外部 ffprobe 复核 pix_fmt）
        if (File.Exists(sample10bit))
        {
            RunExtra("10bit->x265", () => svc.TranscodeAsync(sample10bit, Out("x-10bit.mp4"), new VideoTranscodeOptions
            {
                Container = VideoContainer.Mp4, VideoCodec = VideoCodec.H265, AudioCodec = AudioCodec.None,
                RateControl = RateControlMode.Crf, Crf = 32, Preset = PresetLevel.Ultrafast,
            }).GetAwaiter().GetResult());
        }
        if (File.Exists(sample422))
        {
            RunExtra("422->x264", () => svc.TranscodeAsync(sample422, Out("x-422.mp4"), new VideoTranscodeOptions
            {
                Container = VideoContainer.Mp4, VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.None,
                RateControl = RateControlMode.Crf, Crf = 32, Preset = PresetLevel.Ultrafast,
            }).GetAwaiter().GetResult());
        }

        RunExtra("filters+scale+gain", () => svc.TranscodeAsync(sample, Out("x-filters.mp4"), new VideoTranscodeOptions
        {
            Container = VideoContainer.Mp4, VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.Mp3,
            RateControl = RateControlMode.Crf, Crf = 30, Preset = PresetLevel.Ultrafast,
            CropEnabled = true, CropLeft = 6, CropRight = 6, CropTop = 3, CropBottom = 3,
            Deinterlace = DeinterlaceMode.Yadif, Denoise = DenoiseMode.Hqdn3dMedium,
            ScaleMode = ScaleMode.FitWithin, ScaleWidth = 100, ScaleHeight = 100,
            FpsMode = FpsMode.Fixed, FpsValue = 8, AudioGainDb = -6, AudioBitrateKbps = 64,
        }).GetAwaiter().GetResult());

        RunExtra("profile/level", () => svc.TranscodeAsync(sample, Out("x-profile.mov"), new VideoTranscodeOptions
        {
            Container = VideoContainer.Mov, VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.None,
            RateControl = RateControlMode.Crf, Crf = 30, Preset = PresetLevel.Ultrafast,
            Profile = "high", Level = "4.1",
        }).GetAwaiter().GetResult());

        RunExtra("bitrate+maxrate(avi)", () => svc.TranscodeAsync(sample, Out("x-bitrate.avi"), new VideoTranscodeOptions
        {
            Container = VideoContainer.Avi, VideoCodec = VideoCodec.Mpeg4, AudioCodec = AudioCodec.Mp3,
            RateControl = RateControlMode.Bitrate, VideoBitrateKbps = 600, AudioBitrateKbps = 64,
            Preset = PresetLevel.Ultrafast,
        }).GetAwaiter().GetResult());

        RunExtra("scale-Exact/Width/Height", () =>
        {
            svc.TranscodeAsync(sample, Out("x-scale-exact.mp4"), O(ScaleMode.Exact, w: 100, h: 40)).GetAwaiter().GetResult();
            svc.TranscodeAsync(sample, Out("x-scale-w.mp4"), O(ScaleMode.Width, w: 100)).GetAwaiter().GetResult();
            svc.TranscodeAsync(sample, Out("x-scale-h.mp4"), O(ScaleMode.Height, h: 40)).GetAwaiter().GetResult();

            static VideoTranscodeOptions O(ScaleMode m, int w = 0, int h = 0) => new()
            {
                Container = VideoContainer.Mp4, VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.None,
                RateControl = RateControlMode.Crf, Crf = 32, Preset = PresetLevel.Ultrafast,
                ScaleMode = m, ScaleWidth = w, ScaleHeight = h,
            };
        });

        // 4. 硬件编码尝试（驱动相关，失败记 SKIP 不算代码回归）
        foreach ((string name, HardwareBackend hw) in new[]
                 {
                     ("h264_nvenc", HardwareBackend.Nvidia),
                     ("h264_qsv", HardwareBackend.Intel),
                     ("h264_amf", HardwareBackend.Amd),
                 })
        {
            if (!ffmpegSvc.ValidateEncoder(name)) { Console.WriteLine($"  [hw:{name}] SKIP (encoder absent)"); _skips++; continue; }
            try
            {
                svc.TranscodeAsync(sample, Out($"x-hw-{name}.mp4"), new VideoTranscodeOptions
                {
                    Container = VideoContainer.Mp4, VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.None,
                    HardwareBackend = hw, RateControl = RateControlMode.Crf, Crf = 30,
                    Preset = PresetLevel.Ultrafast,
                }).GetAwaiter().GetResult();
                Console.WriteLine($"  [hw:{name}] OK ({new FileInfo(Out($"x-hw-{name}.mp4")).Length} bytes)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [hw:{name}] SKIP: {ex.Message.Split('\n')[0]}");
                _skips++;
            }
        }

        Console.WriteLine($"----\ntotal={_runCount} failures={_failures} skips={_skips}");
        if (_failedNames.Count > 0)
        {
            Console.WriteLine("FAILED: " + string.Join(", ", _failedNames));
            return 3;
        }
        Console.WriteLine("ALL PASS");
        return 0;

        string Out(string name) => Path.Combine(workDir, name);
    }

    private static void RunCase(VideoTranscodeService svc, string name, string input, VideoContainer container,
        VideoCodec v, AudioCodec a, Action<VideoTranscodeOptions> tune)
    {
        string outPath = Path.Combine(Path.GetDirectoryName(input)!, $"m-{container}-{v}-{a}.{Ext(container)}");
        try
        {
            var opt = new VideoTranscodeOptions { Container = container, VideoCodec = v, AudioCodec = a, AudioGainDb = 0 };
            tune(opt);
            svc.TranscodeAsync(input, outPath, opt).GetAwaiter().GetResult();
            Verify(outPath, v, a);
            _runCount++;
            Console.WriteLine($"  [{name}] OK");
        }
        catch (Exception ex)
        {
            _runCount++;
            _failures++;
            _failedNames.Add(name);
            Console.WriteLine($"  [{name}] FAIL: {ex.Message.Split('\n')[0]}");
        }
    }

    private static void RunExtra(string name, Action run)
    {
        try
        {
            run();
            _runCount++;
            Console.WriteLine($"  [{name}] OK");
        }
        catch (Exception ex)
        {
            _runCount++;
            _failures++;
            _failedNames.Add(name);
            Console.WriteLine($"  [{name}] FAIL: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
    }

    /// <summary>进程内探测输出并断言编码名/时长。</summary>
    private static void Verify(string path, VideoCodec expectedV, AudioCodec expectedA)
    {
        var probe = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance)
            .ProbeAsync(path).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("probe returned null");
        string wantV = expectedV switch
        {
            VideoCodec.H264 => "h264",
            VideoCodec.H265 => "hevc",
            VideoCodec.Av1Aom or VideoCodec.Av1Svt => "av1",
            VideoCodec.Vp8 => "vp8",
            VideoCodec.Vp9 => "vp9",
            VideoCodec.Mpeg4 => "mpeg4",
            VideoCodec.Gif => "gif",
            _ => "",
        };
        string wantA = expectedA switch
        {
            AudioCodec.Aac => "aac",
            AudioCodec.Mp3 => "mp3",
            AudioCodec.Opus => "opus",
            AudioCodec.Vorbis => "vorbis",
            AudioCodec.Flac => "flac",
            AudioCodec.Ac3 => "ac3",
            _ => "",
        };
        if (probe.VideoCodec != wantV)
            throw new InvalidOperationException($"video codec: want {wantV} got {probe.VideoCodec}");
        if (wantA.Length == 0 && probe.HasAudio)
            throw new InvalidOperationException($"audio: want none got {probe.AudioCodec}");
        if (wantA.Length > 0 && (!probe.HasAudio || probe.AudioCodec != wantA))
            throw new InvalidOperationException($"audio codec: want {wantA} got {(probe.HasAudio ? probe.AudioCodec : "none")}");
        if (probe.DurationMs < 1500)
            throw new InvalidOperationException($"duration too short: {probe.DurationMs}ms");
    }

    private static string Ext(VideoContainer c) => c switch
    {
        VideoContainer.Mkv => "mkv",
        VideoContainer.Mov => "mov",
        VideoContainer.Avi => "avi",
        VideoContainer.Gif => "gif",
        _ => "mp4",
    };
}
