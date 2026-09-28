using ATL;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace AudioSmokeTest;

/// <summary>
/// 进程内音频转换全格式烟测：11 种输出格式 × 自校验（进程内探测），外加
/// 位深档、采样率对齐、多声道下混、增益、链式解码（互转）、ATL 标签/封面跟随写入。
/// 测试输入全部自生成（正弦 PCM 直接写 WAV、SkiaSharp 生成封面 PNG），不依赖外部工具。
/// </summary>
internal static class Program
{
    private static int _failures;
    private static int _runs;

    private static int Main(string[] args)
    {
        string workDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "fancytool-audio-smoke");
        Directory.CreateDirectory(workDir);
        Console.WriteLine($"work dir: {workDir}");

        var svc = new AudioConvertService(NullLogger<AudioConvertService>.Instance);

        // 0. 输入源
        string sine = Path.Combine(workDir, "sine.wav");
        WriteSineWav(sine, 44100, 2, 2.0);
        string sine96k = Path.Combine(workDir, "sine96k.wav");
        WriteSineWav(sine96k, 96000, 2, 1.0);
        string sine6ch = Path.Combine(workDir, "sine6ch.wav");
        WriteSineWav(sine6ch, 44100, 6, 1.0);

        // 1. 全格式：sine.wav → 11 种输出（进程内探测断言编码名 + 时长）
        (AudioConvertFormat F, string WantCodec)[] formats =
        [
            (AudioConvertFormat.Mp3, "mp3"),
            (AudioConvertFormat.AacM4a, "aac"),
            (AudioConvertFormat.Flac, "flac"),
            (AudioConvertFormat.AlacM4a, "alac"),
            (AudioConvertFormat.Wav, "pcm_s16le"),
            (AudioConvertFormat.Aiff, "pcm_s16be"),
            (AudioConvertFormat.OggVorbis, "vorbis"),
            (AudioConvertFormat.Opus, "opus"),
            (AudioConvertFormat.Wma, "wmav2"),
            (AudioConvertFormat.Ac3, "ac3"),
            (AudioConvertFormat.Mp2, "mp2"),
        ];
        foreach (var (f, want) in formats)
        {
            string outPath = Out(f.ToString());
            Run($"{f}", () => ConvertAndVerify(svc, sine, outPath, f, want, durationAtLeastMs: 1800));
        }

        // 2. 位深档（wav/aiff/flac）
        Run("wav-S24", () => ConvertAndVerifyDepth(svc, sine, Out("d24.wav"), AudioConvertFormat.Wav, AudioBitDepthMode.S24, "pcm_s24le", 24));
        Run("wav-S32", () => ConvertAndVerifyDepth(svc, sine, Out("d32.wav"), AudioConvertFormat.Wav, AudioBitDepthMode.S32, "pcm_s32le", 32));
        Run("wav-F32", () => ConvertAndVerifyDepth(svc, sine, Out("df32.wav"), AudioConvertFormat.Wav, AudioBitDepthMode.F32, "pcm_f32le", 32));
        Run("aiff-S24", () => ConvertAndVerifyDepth(svc, sine, Out("d24.aiff"), AudioConvertFormat.Aiff, AudioBitDepthMode.S24, "pcm_s24be", 24));

        // 3. 位深透传：16bit → wav24 → flac(Auto) 应保持 24bit
        Run("depth-passthrough", () =>
        {
            svc.ConvertAsync(sine, Out("mid24.wav"), new AudioConvertOptions { Format = AudioConvertFormat.Wav, BitDepth = AudioBitDepthMode.S24 }).GetAwaiter().GetResult();
            ConvertAndVerifyDepth(svc, Out("mid24.wav"), Out("keep24.flac"), AudioConvertFormat.Flac, AudioBitDepthMode.Auto, "flac", 24);
        });

        // 4. 采样率对齐：96k → mp3 应回落 48k；44.1k → opus 应贴到 48k
        Run("rate-snap-mp3", () =>
        {
            svc.ConvertAsync(sine96k, Out("r96.mp3"), new AudioConvertOptions { Format = AudioConvertFormat.Mp3 }).GetAwaiter().GetResult();
            var p = Require(svc, Out("r96.mp3"));
            Check(p.SampleRate == 48000, $"mp3 rate: want 48000 got {p.SampleRate}");
        });
        Run("rate-snap-opus", () =>
        {
            svc.ConvertAsync(sine, Out("r-opus.opus"), new AudioConvertOptions { Format = AudioConvertFormat.Opus }).GetAwaiter().GetResult();
            var p = Require(svc, Out("r-opus.opus"));
            Check(p.SampleRate == 48000, $"opus rate: want 48000 got {p.SampleRate}");
        });
        Run("rate-explicit-96000-flac", () =>
        {
            svc.ConvertAsync(sine, Out("r96k.flac"), new AudioConvertOptions { Format = AudioConvertFormat.Flac, SampleRateHz = 96000 }).GetAwaiter().GetResult();
            var p = Require(svc, Out("r96k.flac"));
            Check(p.SampleRate == 96000, $"flac rate: want 96000 got {p.SampleRate}");
        });

        // 5. 多声道下混：6ch → mp3 应变立体声（编码器不支持 5.1，swr 实际下混）
        Run("downmix-mp3", () =>
        {
            svc.ConvertAsync(sine6ch, Out("dm.mp3"), new AudioConvertOptions { Format = AudioConvertFormat.Mp3 }).GetAwaiter().GetResult();
            var p = Require(svc, Out("dm.mp3"));
            Check(p.Channels == 2, $"mp3 channels: want 2 got {p.Channels}");
        });
        Run("keep6ch-flac", () =>
        {
            svc.ConvertAsync(sine6ch, Out("dm.flac"), new AudioConvertOptions { Format = AudioConvertFormat.Flac }).GetAwaiter().GetResult();
            var p = Require(svc, Out("dm.flac"));
            Check(p.Channels == 6, $"flac channels: want 6 got {p.Channels}");
        });

        // 6. 链式解码（互转覆盖各解码器）：每个已生成输出再转 FLAC/WAV
        foreach (var (f, _) in formats)
        {
            string src = Out(f.ToString());
            if (!File.Exists(src)) continue;
            Run($"chain-{f}->wav", () =>
                svc.ConvertAsync(src, Out($"chain-{f}.wav"), new AudioConvertOptions { Format = AudioConvertFormat.Wav }).GetAwaiter().GetResult());
        }

        // 7. 增益
        Run("gain-6db", () =>
            svc.ConvertAsync(sine, Out("gain.flac"), new AudioConvertOptions { Format = AudioConvertFormat.Flac, GainDb = -6 }).GetAwaiter().GetResult());

        // 8. 标签/封面跟随：sine→mp3，ATL 写入标签+封面，再转 flac/m4a/ogg 验证 ATL 读回
        Run("metadata-carry", () =>
        {
            string tagged = Out("tagged.mp3");
            svc.ConvertAsync(sine, tagged, new AudioConvertOptions { Format = AudioConvertFormat.Mp3, CopyMetadata = false }).GetAwaiter().GetResult();
            var t = new Track(tagged);
            t.Title = "Smoke Title";
            t.Artist = "Smoke Artist";
            t.Album = "Smoke Album";
            t.TrackNumber = 7;
            t.Year = 2026;
            t.EmbeddedPictures.Add(PictureInfo.fromBinaryData(MakePng()));
            if (!t.Save()) throw new InvalidOperationException("ATL tag save failed");

            foreach ((string name, AudioConvertFormat fmt) in new[]
                     {
                         ("flac", AudioConvertFormat.Flac), ("m4a", AudioConvertFormat.AacM4a),
                         ("alac", AudioConvertFormat.AlacM4a), ("ogg", AudioConvertFormat.OggVorbis),
                         ("opus", AudioConvertFormat.Opus), ("mp3", AudioConvertFormat.Mp3),
                     })
            {
                string dst = Out($"meta-{name}.{AudioConvertService.GetExtension(fmt).TrimStart('.')}");
                svc.ConvertAsync(tagged, dst, new AudioConvertOptions { Format = fmt }).GetAwaiter().GetResult();
                var back = new Track(dst);
                Check(back.Title == "Smoke Title", $"{name} title: {back.Title}");
                Check(back.Artist == "Smoke Artist", $"{name} artist: {back.Artist}");
                Check(back.Album == "Smoke Album", $"{name} album: {back.Album}");
                Check(back.EmbeddedPictures.Any(p => p.PictureData is { Length: > 0 }), $"{name} cover missing");
                Console.WriteLine($"    meta->{name}: title/artist/album/cover OK");
            }
        });

        // 9. wav 不写标签（RIFF INFO 无 UTF-8 互操作）
        Run("metadata-skip-wav", () =>
        {
            string src = Out("tagged.mp3");
            if (!File.Exists(src)) throw new InvalidOperationException("tagged.mp3 missing");
            string dst = Out("meta-nowav.wav");
            svc.ConvertAsync(src, dst, new AudioConvertOptions { Format = AudioConvertFormat.Wav }).GetAwaiter().GetResult();
            var back = new Track(dst);
            Check(string.IsNullOrEmpty(back.Title) || back.Title == Path.GetFileNameWithoutExtension(dst), $"wav title unexpectedly carried: {back.Title}");
        });

        // 10. 拼接/损坏 MP3：两段不同采样率/声道数的 MP3 字节直拼（模拟用户库中的拼接文件），
        //     中途参数变化必须重建重采样器继续转，而不是 AVERROR_INPUT_CHANGED 整体失败
        Run("glued-mp3-param-change", () =>
        {
            string part1 = Out("glue-p1.mp3");
            string part2 = Out("glue-p2.mp3");
            WriteSineWav(Out("g1.wav"), 44100, 2, 0.5);
            WriteSineWav(Out("g2.wav"), 24000, 1, 0.5);
            svc.ConvertAsync(Out("g1.wav"), part1, new AudioConvertOptions { Format = AudioConvertFormat.Mp3 }).GetAwaiter().GetResult();
            svc.ConvertAsync(Out("g2.wav"), part2, new AudioConvertOptions { Format = AudioConvertFormat.Mp3 }).GetAwaiter().GetResult();
            File.WriteAllBytes(Out("glued.mp3"), File.ReadAllBytes(part1).Concat(File.ReadAllBytes(part2)).ToArray());

            svc.ConvertAsync(Out("glued.mp3"), Out("glued.m4a"), new AudioConvertOptions { Format = AudioConvertFormat.AacM4a }).GetAwaiter().GetResult();
            var p = Require(svc, Out("glued.m4a"));
            Check(p.Codec == "aac", $"codec: {p.Codec}");
            // 探测取拼接文件最后的帧头（24k 单声道），编码器按其开声道；关键断言是两段都被完整转出
            Check(p.DurationMs >= 850, $"both segments converted: duration {p.DurationMs}ms");
            Check(p.Channels is 1 or 2, $"channels: {p.Channels}");
        });

        // 11. 损坏封面：内嵌截断 JPEG（解析不出宽高）时必须跳过封面继续转，
        //     而不是把 0x0 尺寸的封面流写进 m4a 导致 write_header EINVAL
        Run("corrupt-cover-skip", () =>
        {
            string tagged = Out("tagged.mp3");
            if (!File.Exists(tagged)) throw new InvalidOperationException("tagged.mp3 missing");
            string bad = Out("badcover.mp3");
            File.Copy(tagged, bad, overwrite: true);
            var t = new Track(bad);
            t.EmbeddedPictures.Clear();
            t.EmbeddedPictures.Add(PictureInfo.fromBinaryData([0xFF, 0xD8, 0xFF, 0xD9])); // 仅 SOI+EOI，无 SOF 尺寸
            if (!t.Save()) throw new InvalidOperationException("ATL tag save failed");

            svc.ConvertAsync(bad, Out("badcover.m4a"), new AudioConvertOptions { Format = AudioConvertFormat.AacM4a }).GetAwaiter().GetResult();
            var p = Require(svc, Out("badcover.m4a"));
            Check(p.Codec == "aac", $"codec: {p.Codec}");
        });

        Console.WriteLine($"----\nruns={_runs} failures={_failures}");
        if (_failures > 0) return 3;
        Console.WriteLine("ALL PASS");
        return 0;

        string Out(string name) => Path.Combine(workDir, name);
    }

    private static void Run(string name, Action run)
    {
        try
        {
            run();
            _runs++;
            Console.WriteLine($"  [{name}] OK");
        }
        catch (Exception ex)
        {
            _runs++;
            _failures++;
            Console.WriteLine($"  [{name}] FAIL: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
    }

    private static void ConvertAndVerify(AudioConvertService svc, string input, string output,
        AudioConvertFormat fmt, string wantCodec, long durationAtLeastMs)
    {
        svc.ConvertAsync(input, output, new AudioConvertOptions { Format = fmt }).GetAwaiter().GetResult();
        var p = Require(svc, output);
        Check(p.Codec == wantCodec, $"codec: want {wantCodec} got {p.Codec}");
        Check(p.DurationMs >= durationAtLeastMs, $"duration: {p.DurationMs}ms < {durationAtLeastMs}ms");
        Check(p.SampleRate > 0 && p.Channels > 0, "probe rate/channels empty");
    }

    private static void ConvertAndVerifyDepth(AudioConvertService svc, string input, string output,
        AudioConvertFormat fmt, AudioBitDepthMode depth, string wantCodec, int wantBits)
    {
        svc.ConvertAsync(input, output, new AudioConvertOptions { Format = fmt, BitDepth = depth }).GetAwaiter().GetResult();
        var p = Require(svc, output);
        Check(p.Codec == wantCodec, $"codec: want {wantCodec} got {p.Codec}");
        Check(p.BitDepth == wantBits, $"depth: want {wantBits} got {p.BitDepth}");
    }

    private static AudioProbeInfo Require(AudioConvertService svc, string path)
        => svc.ProbeAsync(path).GetAwaiter().GetResult()
           ?? throw new InvalidOperationException($"probe returned null: {path}");

    private static void Check(bool cond, string message)
    {
        if (!cond) throw new InvalidOperationException(message);
    }

    /// <summary>直接写 RIFF PCM16 WAV（无外部依赖）。</summary>
    private static void WriteSineWav(string path, int sampleRate, int channels, double seconds)
    {
        int frames = (int)(sampleRate * seconds);
        short[] samples = new short[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * 440.0 * i / sampleRate) * 12000);
            for (int c = 0; c < channels; c++) samples[i * channels + c] = v;
        }
        int dataLen = samples.Length * 2;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8);
        w.Write(36 + dataLen);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2); // byte rate
        w.Write((short)(channels * 2));     // block align
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataLen);
        foreach (short s in samples) w.Write(s);
    }

    /// <summary>SkiaSharp 生成 8x8 PNG 封面。</summary>
    private static byte[] MakePng()
    {
        using var surface = SKSurface.Create(new SKImageInfo(8, 8));
        surface.Canvas.Clear(new SKColor(30, 144, 255));
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
