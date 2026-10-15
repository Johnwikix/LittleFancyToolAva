using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FfmpegSmokeTest;

/// <summary>
/// 进程内 FFmpeg 转码全矩阵烟测：按 UI 实际允许的 容器×视频编码×音频编码 组合
/// （MP4 5×5、MKV 7×7、MOV 4×4、AVI 2×4、GIF）逐一转码并用进程内探测自校验，
/// 另覆盖 10bit/422 透传、两通、滤镜链、profile/level 与硬件编码尝试。
/// </summary>
internal static partial class Program
{
    private static int _runCount;
    private static int _failures;
    private static int _skips;
    private static readonly List<string> _failedNames = [];

    [STAThread]
    private static int Main(string[] args)
    {
        string workDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "fancytool-smoke");
        // 自包含测试输入：C# 直写 AVI（rawvideo BGR24 + PCM16 正弦）。
        // 10bit/422 特殊源需外部 ffmpeg 生成（可选，存在才测）：
        //   ffmpeg -f lavfi -i testsrc2=duration=2:size=160x120:rate=10 -c:v libx265 -preset ultrafast -pix_fmt yuv420p10le sample10bit.mkv
        //   ffmpeg -f lavfi -i testsrc2=duration=2:size=160x120:rate=10 -c:v libx264 -preset ultrafast -pix_fmt yuv422p sample422.mkv
        string sample = Path.Combine(workDir, "sample.avi");
        string sample10bit = Path.Combine(workDir, "sample10bit.mkv");
        string sample422 = Path.Combine(workDir, "sample422.mkv");
        Directory.CreateDirectory(workDir);
        if (args.Contains("--settings-only")) return RunSettingsCases(workDir, args.Contains("--inspect"));
        WriteTestAvi(sample, width: 160, height: 120, fps: 10, seconds: 2);
        Console.WriteLine($"sample avi: {new FileInfo(sample).Length} bytes; 10bit={File.Exists(sample10bit)} 422={File.Exists(sample422)}");

        // 1. 库加载 + 关键编码器存在性
        var svc = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance);
        var ffmpegSvc = new FfmpegService(NullLogger<FfmpegService>.Instance);
        if (!ffmpegSvc.ValidateAsync().GetAwaiter().GetResult())
        {
            Console.WriteLine($"FATAL load: {ffmpegSvc.LastError}");
            return 1;
        }
        Console.WriteLine($"load OK: {ffmpegSvc.VersionInfo} v={ffmpegSvc.AvailableVideoEncoders.Count} a={ffmpegSvc.AvailableAudioEncoders.Count}");

        if (args.Contains("--hdr-capabilities")) return RunHdrCapabilities();
        if (args.Contains("--hdr-only")) return RunHdrCases(svc, workDir);

        if (args.Contains("--sr-benchmark")) return RunSrBenchmark(workDir);
        if (args.Contains("--interpolation-benchmark")) return RunInterpolationBenchmark(workDir, args.Contains("--full"));
        if (args.Contains("--interpolation-order-benchmark")) return RunInterpolationOrderBenchmark(workDir);
        if (args.Contains("--editing-ui")) return RunEditingUi(svc, sample, workDir, args.Contains("--sr-ui"), args.Contains("--interpolation-ui"));
        if (args.Contains("--sr-only")) return RunSuperResolutionCases(sample, workDir);
        if (args.Contains("--interpolation-only")) return RunInterpolationCases(sample, workDir);
        if (args.Contains("--editing-only")) return RunEditingCases(svc, sample, workDir);

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

    /// <summary>直写最小 AVI（BGR24 rawvideo + PCM16 单声道正弦），供烟测自包含生成输入。</summary>
    private static void WriteTestAvi(string path, int width, int height, int fps, double seconds, bool whitePatch = false)
    {
        int frames = (int)(fps * seconds);
        int sampleRate = 44100;
        int totalAudioSamples = (int)(sampleRate * seconds);
        int stride = (width * 3 + 3) & ~3;
        int frameBytes = stride * height;

        // 先在内存里生成帧（渐变+移动方块）与音频
        var frameData = new byte[frames][];
        for (int f = 0; f < frames; f++)
        {
            var buf = new byte[frameBytes];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sx = x * 4 + f * 2;
                    int sy = y * 3;
                    byte r = (byte)(sx % 256);
                    byte g = (byte)(sy % 256);
                    byte b = (byte)((sx + sy) % 256);
                    bool inBox = Math.Abs(x - 40 - f * 3) < 20 && Math.Abs(y - 60) < 20;
                    int row = height - 1 - y; // bottom-up
                    int off = row * stride + x * 3;
                    buf[off] = inBox ? (byte)255 : b;
                    buf[off + 1] = inBox ? (byte)200 : g;
                    buf[off + 2] = inBox ? (byte)0 : r;
                    if (whitePatch && x < width / 3 && y < height / 3) buf[off] = buf[off + 1] = buf[off + 2] = 255;
                }
            }
            frameData[f] = buf;
        }
        short[] audio = new short[totalAudioSamples];
        for (int i = 0; i < totalAudioSamples; i++)
            audio[i] = (short)(Math.Sin(2 * Math.PI * 440.0 * i / sampleRate) * 12000);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void fourcc(string tag) => w.Write(System.Text.Encoding.ASCII.GetBytes(tag));

        fourcc("RIFF");
        var riffSizePos = ms.Position; w.Write(0u);
        fourcc("AVI ");

        // ── hdrl ──
        fourcc("LIST");
        w.Write(4 + 8 + 56 + 8 + 40 + 8 + 8 + 56 + 8 + 16);
        fourcc("hdrl");
        fourcc("avih");
        w.Write(56);
        w.Write((uint)(1_000_000 / fps));           // microsec per frame
        w.Write((uint)(frameBytes * fps + sampleRate * 2)); // max bytes/sec approx
        w.Write(0u);                                // padding
        w.Write(0x10u);                             // AVIF_HASINDEX
        w.Write((uint)frames);
        w.Write(0u);                                // initial frames
        w.Write(2u);                                // streams
        w.Write((uint)frameBytes);                  // suggested buffer
        w.Write((uint)width);
        w.Write((uint)height);
        for (int i = 0; i < 4; i++) w.Write(0u);

        // ── video strl ──
        fourcc("LIST");
        w.Write(4 + 8 + 56 + 8 + 40);
        fourcc("strl");
        fourcc("strh");
        w.Write(56);
        fourcc("vids"); fourcc("DIB ");
        w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
        w.Write(0u);                                // initial frames
        w.Write(1u); w.Write((uint)fps);            // scale/rate
        w.Write(0u); w.Write((uint)frames);
        w.Write((uint)frameBytes);
        w.Write(0xFFFFFFFF);                        // quality
        w.Write(0u);                                // sample size
        w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)width); w.Write((ushort)height);

        fourcc("strf");
        w.Write(40);
        w.Write(40);                                // biSize
        w.Write(width); w.Write(height);
        w.Write((ushort)1); w.Write((ushort)24);
        w.Write(0u);                                // BI_RGB
        w.Write((uint)frameBytes);
        for (int i = 0; i < 4; i++) w.Write(0u);

        // ── audio strl ──
        fourcc("LIST");
        w.Write(4 + 8 + 56 + 8 + 16);
        fourcc("strl");
        fourcc("strh");
        w.Write(56);
        fourcc("auds"); fourcc("\0\0\0\0");
        w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
        w.Write(0u);
        w.Write(1u); w.Write((uint)sampleRate);
        w.Write(0u); w.Write((uint)totalAudioSamples);
        w.Write((uint)sampleRate * 2);
        w.Write(0xFFFFFFFF);
        w.Write(2u);                                // sample size (bytes)
        w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);

        fourcc("strf");
        w.Write(16);
        w.Write((ushort)1);                         // PCM
        w.Write((ushort)1);                         // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((ushort)2);
        w.Write((ushort)16);

        // ── movi ──
        fourcc("LIST");
        var moviSizePos = ms.Position; w.Write(0u); // patch later
        long moviStart = ms.Position;
        fourcc("movi");
        var idx = new List<(string Id, uint Off, uint Len)>();
        int audioSent = 0;
        for (int f = 0; f < frames; f++)
        {
            uint off = (uint)(ms.Position - moviStart);
            fourcc("00db"); w.Write((uint)frameBytes); w.Write(frameData[f]);
            idx.Add(("00db", off, (uint)frameBytes));

            int take = (totalAudioSamples - audioSent) / (frames - f);
            if (take > 0)
            {
                off = (uint)(ms.Position - moviStart);
                int bytes = take * 2;
                fourcc("01wb"); w.Write((uint)bytes);
                for (int i = 0; i < take; i++) w.Write(audio[audioSent + i]);
                audioSent += take;
                idx.Add(("01wb", off, (uint)bytes));
            }
        }
        uint moviSize = (uint)(ms.Position - moviStart);

        // ── idx1 ──
        fourcc("idx1");
        w.Write((uint)(idx.Count * 16));
        foreach (var (id, off, len) in idx)
        {
            fourcc(id);
            w.Write(0x10u);                         // AVIIF_KEYFRAME
            w.Write(off);
            w.Write(len);
        }

        // patch sizes
        long end = ms.Position;
        ms.Position = riffSizePos; w.Write((uint)(end - 8));
        ms.Position = moviSizePos; w.Write(moviSize);
        ms.Position = end;

        File.WriteAllBytes(path, ms.ToArray());
    }
}
