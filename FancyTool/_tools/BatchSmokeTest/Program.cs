using System.Diagnostics;
using Avalonia.Platform.Storage;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FancyToolAva.Utils;
using FancyToolAva.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BatchSmokeTest;

/// <summary>
/// 批量转码工况烟测：输出目录镜像（OutputPathPlanner 纯逻辑）+ 并行编码真实跑通
/// （音频 mp3/flac 批量、视频 h264 批量、同名冲突去重、并行取消、.tmp 清理）。
/// 输入自生成（正弦 WAV 直写、AVI 直写复用 FfmpegSmokeTest 的写法），不依赖外部工具。
/// </summary>
internal static class Program
{
    private static int _runs;
    private static int _failures;
    private static readonly List<string> _failedNames = [];

    private static int Main(string[] args)
    {
        string workDir = args.Length > 0
            ? args[0]
            : Path.Combine(Path.GetTempPath(), "fancytool-batch-smoke");
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        Directory.CreateDirectory(workDir);
        Console.WriteLine($"work dir: {workDir}");

        PlanScenarios(workDir);

        var audio = new AudioConvertService(NullLogger<AudioConvertService>.Instance);
        AudioParallelMirror(audio, workDir);
        AudioFlatCollision(audio, workDir);
        AudioCancelMidway(audio, workDir);

        var video = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance);
        VideoParallelMirror(video, workDir);

        VmAudioScanStress(audio, workDir);
        VmAudioConvertMirror(audio, workDir);
        VmVideoScanConvert(video, workDir);
        VmImageScanFilter(workDir);
        VmAudioPauseResume(audio, workDir);
        VmAudioStop(audio, workDir);
        VmAudioFailureLog(workDir);

        Console.WriteLine($"----\nruns={_runs} failures={_failures}");
        if (_failedNames.Count > 0)
        {
            Console.WriteLine("FAILED: " + string.Join(", ", _failedNames));
            return 3;
        }
        Console.WriteLine("ALL PASS");
        return 0;
    }

    // ══════════════ A. OutputPathPlanner 纯逻辑 ══════════════

    private static void PlanScenarios(string workDir)
    {
        string root = Path.Combine(workDir, "plan");
        string src = Path.Combine(root, "src");
        Directory.CreateDirectory(Path.Combine(src, "a"));
        Directory.CreateDirectory(Path.Combine(src, "b", "c"));
        string outDir = Path.Combine(root, "out");

        Run("plan-mirror-nested", () =>
        {
            string f = Path.Combine(src, "a", "x.wav");
            string dir = OutputPathPlanner.ResolveOutputDir(outDir, f, src);
            Check(dir == Path.Combine(outDir, "a"), $"want {outDir}\\a got {dir}");
        });

        Run("plan-mirror-deep", () =>
        {
            string f = Path.Combine(src, "b", "c", "x.wav");
            string dir = OutputPathPlanner.ResolveOutputDir(outDir, f, src);
            Check(dir == Path.Combine(outDir, "b", "c"), $"want {outDir}\\b\\c got {dir}");
        });

        Run("plan-root-equals-srcdir-flat", () =>
        {
            string f = Path.Combine(src, "x.wav");
            string dir = OutputPathPlanner.ResolveOutputDir(outDir, f, src);
            Check(dir == outDir, $"want {outDir} got {dir}");
        });

        Run("plan-single-file-flat", () =>
        {
            string f = Path.Combine(workDir, "loose.wav");
            string dir = OutputPathPlanner.ResolveOutputDir(outDir, f, null);
            Check(dir == outDir, $"null root must stay flat, got {dir}");
        });

        Run("plan-escape-root-flat", () =>
        {
            // 文件已不在登记的根目录下（如中途被移动）：不允许 .. 逃逸，回退平铺
            string f = Path.Combine(workDir, "elsewhere", "x.wav");
            string dir = OutputPathPlanner.ResolveOutputDir(outDir, f, src);
            Check(dir == outDir, $"must fall back flat, got {dir}");
        });

        Run("plan-batch-dedupe", () =>
        {
            var taken = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            string p1 = OutputPathPlanner.PlanUniquePath(outDir, "x", ".mp3", false, taken);
            string p2 = OutputPathPlanner.PlanUniquePath(outDir, "x", ".mp3", false, taken);
            string p3 = OutputPathPlanner.PlanUniquePath(outDir, "X", ".mp3", false, taken); // 大小写不敏感
            Check(Path.GetFileName(p1) == "x.mp3", p1);
            Check(Path.GetFileName(p2) == "x_1.mp3", p2);
            Check(string.Equals(Path.GetFileName(p3), "x_2.mp3", StringComparison.OrdinalIgnoreCase), p3);
        });

        Run("plan-existing-file-dedupe", () =>
        {
            string dir = Path.Combine(root, "out2");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "y.mp3"), "existing");
            var taken = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            string p = OutputPathPlanner.PlanUniquePath(dir, "y", ".mp3", false, taken);
            Check(Path.GetFileName(p) == "y_1.mp3", p);
        });

        Run("plan-add-suffix", () =>
        {
            var taken = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            string dir = Path.Combine(root, "out3");
            string p = OutputPathPlanner.PlanUniquePath(dir, "z", ".mp3", true, taken);
            Check(Path.GetFileName(p) == "z_output.mp3", p);
        });
    }

    // ══════════════ B. 音频并行 + 目录镜像（真实编码） ══════════════

    private static void AudioParallelMirror(AudioConvertService svc, string workDir)
    {
        Run("audio-parallel-mirror-mp3", () =>
        {
            string root = Path.Combine(workDir, "amirror");
            string sub = Path.Combine(root, "sub");
            string deep = Path.Combine(sub, "deep");
            Directory.CreateDirectory(deep);
            string looseDir = Path.Combine(workDir, "aloose");
            Directory.CreateDirectory(looseDir);

            string top = Path.Combine(root, "top.wav");
            string dup1 = Path.Combine(sub, "dup.wav");
            string dup2 = Path.Combine(deep, "dup.wav");
            string loose = Path.Combine(looseDir, "single.wav");
            WriteSineWav(top, 44100, 2, 0.5);
            WriteSineWav(dup1, 44100, 2, 0.5);
            WriteSineWav(dup2, 44100, 2, 0.5);
            WriteSineWav(loose, 44100, 2, 0.5);

            string outDir = Path.Combine(workDir, "amirror-out");
            // 与 VM 相同的登记方式：文件夹添加 → root；单独添加 → null
            var items = new List<(string File, string? Root)>
            {
                (top, root), (dup1, root), (dup2, root), (loose, null),
            };
            var plan = BuildAudioPlan(outDir, items, ".mp3", suffixWhenSameDir: true);

            int dop = Math.Clamp(Environment.ProcessorCount, 1, 8);
            Parallel.ForEachAsync(plan, new ParallelOptions { MaxDegreeOfParallelism = dop }, (e, _) =>
                new ValueTask(svc.ConvertAsync(e.File, e.OutputPath,
                    new AudioConvertOptions { Format = AudioConvertFormat.Mp3 }))).GetAwaiter().GetResult();

            string a = Path.Combine(outDir, "top.mp3");
            string b = Path.Combine(outDir, "sub", "dup.mp3");
            string c = Path.Combine(outDir, "sub", "deep", "dup.mp3");
            string d = Path.Combine(outDir, "single.mp3");
            foreach (string f in new[] { a, b, c, d })
            {
                Check(File.Exists(f), $"missing output {f}");
                var p = svc.ProbeAsync(f).GetAwaiter().GetResult() ?? throw new InvalidOperationException("probe null: " + f);
                Check(p.Codec == "mp3", $"{f}: codec {p.Codec}");
            }
            Check(Directory.GetFiles(outDir, "*.tmp", SearchOption.AllDirectories).Length == 0, "tmp leftover");
        });

        Run("audio-parallel-flac-metadata", () =>
        {
            // CopyMetadata=true 并行走 ATL 读标签 + FLAC 封面流写入
            string root = Path.Combine(workDir, "aflac");
            Directory.CreateDirectory(root);
            var items = new List<(string File, string? Root)>();
            for (int i = 0; i < 6; i++)
            {
                string f = Path.Combine(root, $"t{i}.wav");
                WriteSineWav(f, 44100, 2, 0.3);
                items.Add((f, root));
            }
            string outDir = Path.Combine(workDir, "aflac-out");
            var plan = BuildAudioPlan(outDir, items, ".flac", suffixWhenSameDir: true);
            int dop = Math.Clamp(Environment.ProcessorCount, 1, 8);
            Parallel.ForEachAsync(plan, new ParallelOptions { MaxDegreeOfParallelism = dop }, (e, _) =>
                new ValueTask(svc.ConvertAsync(e.File, e.OutputPath,
                    new AudioConvertOptions { Format = AudioConvertFormat.Flac }))).GetAwaiter().GetResult();
            foreach (var e in plan)
            {
                Check(File.Exists(e.OutputPath), "missing " + e.OutputPath);
                var p = svc.ProbeAsync(e.OutputPath).GetAwaiter().GetResult() ?? throw new InvalidOperationException("probe null");
                Check(p.Codec == "flac", $"codec {p.Codec}");
            }
        });
    }

    // ══════════════ C. 同名平铺冲突 ══════════════

    private static void AudioFlatCollision(AudioConvertService svc, string workDir)
    {
        Run("audio-flat-collision", () =>
        {
            string root = Path.Combine(workDir, "acol");
            Directory.CreateDirectory(root);
            string r1 = Path.Combine(root, "r1");
            string r2 = Path.Combine(root, "r2");
            string r3 = Path.Combine(root, "r3");
            Directory.CreateDirectory(r1);
            Directory.CreateDirectory(r2);
            Directory.CreateDirectory(r3);
            foreach (string dir in new[] { r1, r2, r3 })
                WriteSineWav(Path.Combine(dir, "dup.wav"), 44100, 2, 0.3);

            // 三个同名文件均按“单独添加”（root=null）→ 同一输出目录
            string outDir = Path.Combine(workDir, "acol-out");
            var items = new List<(string File, string? Root)>
            {
                (Path.Combine(r1, "dup.wav"), null),
                (Path.Combine(r2, "dup.wav"), null),
                (Path.Combine(r3, "dup.wav"), null),
            };
            var plan = BuildAudioPlan(outDir, items, ".mp3", suffixWhenSameDir: true);
            Check(Path.GetFileName(plan[0].OutputPath) == "dup.mp3", plan[0].OutputPath);
            Check(Path.GetFileName(plan[1].OutputPath) == "dup_1.mp3", plan[1].OutputPath);
            Check(Path.GetFileName(plan[2].OutputPath) == "dup_2.mp3", plan[2].OutputPath);

            Parallel.ForEachAsync(plan, new ParallelOptions { MaxDegreeOfParallelism = 3 }, (e, _) =>
                new ValueTask(svc.ConvertAsync(e.File, e.OutputPath,
                    new AudioConvertOptions { Format = AudioConvertFormat.Mp3 }))).GetAwaiter().GetResult();
            foreach (var e in plan)
                Check(File.Exists(e.OutputPath), "missing " + e.OutputPath);
        });
    }

    // ══════════════ D. 并行取消 ══════════════

    private static void AudioCancelMidway(AudioConvertService svc, string workDir)
    {
        Run("audio-parallel-cancel", () =>
        {
            string root = Path.Combine(workDir, "acancel");
            Directory.CreateDirectory(root);
            var items = new List<(string File, string? Root)>();
            for (int i = 0; i < 12; i++)
            {
                string f = Path.Combine(root, $"c{i}.wav");
                WriteSineWav(f, 44100, 2, 30.0); // 够长，确保 300ms 取消时一定还有未完成任务
                items.Add((f, root));
            }
            string outDir = Path.Combine(workDir, "acancel-out");
            var plan = BuildAudioPlan(outDir, items, ".mp3", suffixWhenSameDir: true);

            using var cts = new CancellationTokenSource(300);
            int completed = 0;
            Parallel.ForEachAsync(plan, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (e, _) =>
            {
                if (cts.Token.IsCancellationRequested) return;
                try
                {
                    await svc.ConvertAsync(e.File, e.OutputPath,
                        new AudioConvertOptions { Format = AudioConvertFormat.Mp3 }, null, cts.Token);
                    Interlocked.Increment(ref completed);
                }
                catch (OperationCanceledException) { }
            }).GetAwaiter().GetResult();

            Check(completed < plan.Count, $"expected partial completion, got {completed}/{plan.Count}");
            foreach (var e in plan.Where(e => File.Exists(e.OutputPath)))
            {
                var p = svc.ProbeAsync(e.OutputPath).GetAwaiter().GetResult();
                Check(p != null, "partial output not playable: " + e.OutputPath);
            }
            Check(Directory.GetFiles(outDir, "*.tmp", SearchOption.AllDirectories).Length == 0, "tmp leftover after cancel");
        });
    }

    // ══════════════ E. 视频并行 + 目录镜像（真实编码） ══════════════

    private static void VideoParallelMirror(VideoTranscodeService svc, string workDir)
    {
        Run("video-parallel-mirror", () =>
        {
            string root = Path.Combine(workDir, "vmirror");
            string sub = Path.Combine(root, "sub");
            string deep = Path.Combine(sub, "deep");
            Directory.CreateDirectory(deep);

            string v0 = Path.Combine(root, "v0.avi");
            string v1 = Path.Combine(sub, "v1.avi");
            string v2 = Path.Combine(deep, "v2.avi");
            foreach (string v in new[] { v0, v1, v2 })
                WriteTestAvi(v, width: 160, height: 120, fps: 10, seconds: 1);

            string outDir = Path.Combine(workDir, "vmirror-out");
            var taken = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var plan = new List<(string File, string OutputPath)>();
            foreach ((string file, string? rf) in new[] { (v0, (string?)root), (v1, root), (v2, root) })
            {
                string dir = OutputPathPlanner.ResolveOutputDir(outDir, file, rf);
                plan.Add((file, OutputPathPlanner.PlanUniquePath(dir,
                    Path.GetFileNameWithoutExtension(file), ".mp4", addSuffix: false, taken)));
            }

            var opt = new VideoTranscodeOptions
            {
                Container = VideoContainer.Mp4,
                VideoCodec = VideoCodec.H264,
                AudioCodec = AudioCodec.Aac,
                RateControl = RateControlMode.Crf,
                Crf = 32,
                Preset = PresetLevel.Ultrafast,
                AudioBitrateKbps = 64,
            };
            int dop = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
            Parallel.ForEachAsync(plan, new ParallelOptions { MaxDegreeOfParallelism = dop }, (e, _) =>
                new ValueTask(svc.TranscodeAsync(e.File, e.OutputPath, opt))).GetAwaiter().GetResult();

            string a = Path.Combine(outDir, "v0.mp4");
            string b = Path.Combine(outDir, "sub", "v1.mp4");
            string c = Path.Combine(outDir, "sub", "deep", "v2.mp4");
            foreach (string f in new[] { a, b, c })
            {
                Check(File.Exists(f), "missing " + f);
                var p = svc.ProbeAsync(f).GetAwaiter().GetResult() ?? throw new InvalidOperationException("probe null");
                Check(p.VideoCodec == "h264", $"{f}: {p.VideoCodec}");
                Check(p.HasAudio && p.AudioCodec == "aac", $"{f}: audio {p.AudioCodec}");
            }
            Check(Directory.GetFiles(outDir, "*.tmp*", SearchOption.AllDirectories).Length == 0, "tmp leftover");
        });
    }

    // ══════════════ F. ViewModel 级：大批量扫描 / 批量添加 / 经 VM 的镜像转换 ══════════════

    private static void VmAudioScanStress(AudioConvertService svc, string workDir)
    {
        Run("vm-audio-scan-1500", () =>
        {
            string root = Path.Combine(workDir, "vm-audio");
            string sub = Path.Combine(root, "sub");
            string deep = Path.Combine(sub, "deep");
            Directory.CreateDirectory(deep);
            for (int i = 0; i < 500; i++) WriteSineWav(Path.Combine(root, $"a{i}.wav"), 44100, 1, 0.05);
            for (int i = 0; i < 500; i++) WriteSineWav(Path.Combine(sub, $"b{i}.wav"), 44100, 1, 0.05);
            for (int i = 0; i < 500; i++) WriteSineWav(Path.Combine(deep, $"c{i}.wav"), 44100, 1, 0.05);

            var vm = new AudioConvertViewModel(svc, new FakeFfmpeg(), new FakeDialogs(), new FakeNotifs(), new FakeViewState());

            // 同一次拖入 根目录 + 其子目录（重叠）：必须去重为 1500，且首个根目录胜出
            vm.AddDroppedPaths([root, sub]);
            WaitFor(() => vm.FileItems.Count == 1500, 120_000, $"FileItems.Count={vm.FileItems.Count}");

            Check(vm.FileItems.All(i => i.RootFolder == root), "all items should carry the first root");
            Check(vm.FileItems.Select(i => i.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase).Count == 1500, "duplicate paths present");
            vm.Dispose();
        });
    }

    private static void VmAudioConvertMirror(AudioConvertService svc, string workDir)
    {
        Run("vm-audio-convert-mirror", () =>
        {
            string root = Path.Combine(workDir, "vm-amirror");
            string sub = Path.Combine(root, "sub");
            string looseDir = Path.Combine(workDir, "vm-aloose");
            Directory.CreateDirectory(sub);
            Directory.CreateDirectory(looseDir);
            for (int i = 0; i < 12; i++) WriteSineWav(Path.Combine(root, $"m{i}.wav"), 44100, 1, 0.2);
            for (int i = 0; i < 12; i++) WriteSineWav(Path.Combine(sub, $"n{i}.wav"), 44100, 1, 0.2);
            string loose = Path.Combine(looseDir, "loose.wav");
            WriteSineWav(loose, 44100, 1, 0.2);

            var vm = new AudioConvertViewModel(svc, new FakeFfmpeg(), new FakeDialogs(), new FakeNotifs(), new FakeViewState());
            vm.AddDroppedPaths([root, loose]);
            WaitFor(() => vm.FileItems.Count == 25, 60_000, $"FileItems.Count={vm.FileItems.Count}");

            string outDir = Path.Combine(workDir, "vm-amirror-out");
            Directory.CreateDirectory(outDir); // 应用中目录由对话框选定必然存在，测试先建好
            vm.OutputFolder = outDir;
            vm.StartConvertCommand.NotifyCanExecuteChanged(); // 无头环境没有跑动的 Dispatcher，手动刷新
            vm.StartConvertCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Check(!vm.IsBusy, "IsBusy stuck after convert");
            for (int i = 0; i < 12; i++)
            {
                Check(File.Exists(Path.Combine(outDir, $"m{i}.mp3")), $"m{i}.mp3");
                Check(File.Exists(Path.Combine(outDir, "sub", $"n{i}.mp3")), $"sub/n{i}.mp3");
            }
            Check(File.Exists(Path.Combine(outDir, "loose.mp3")), "loose.mp3 should be flat at output root");
            Check(Directory.GetFiles(outDir, "*.mp3", SearchOption.AllDirectories).Length == 25, "output total");
            Check(Directory.GetFiles(outDir, "*.tmp", SearchOption.AllDirectories).Length == 0, "tmp leftover");
            vm.Dispose();
        });
    }

    private static void VmVideoScanConvert(VideoTranscodeService svc, string workDir)
    {
        Run("vm-video-scan-convert", () =>
        {
            string root = Path.Combine(workDir, "vm-video");
            string sub = Path.Combine(root, "sub");
            Directory.CreateDirectory(sub);
            WriteTestAvi(Path.Combine(root, "w0.avi"), 160, 120, 10, 1);
            WriteTestAvi(Path.Combine(root, "w1.avi"), 160, 120, 10, 1);
            WriteTestAvi(Path.Combine(sub, "w2.avi"), 160, 120, 10, 1);
            WriteTestAvi(Path.Combine(sub, "w3.avi"), 160, 120, 10, 1);

            var vm = new VideoTranscodeViewModel(svc, new FakeFfmpeg(), new FakeDialogs(), new FakeNotifs(),
                new FakeViewState(), new AppPreferences());
            vm.AddDroppedPaths([root]);
            WaitFor(() => vm.FileItems.Count == 4, 60_000, $"FileItems.Count={vm.FileItems.Count}");

            string outDir = Path.Combine(workDir, "vm-video-out");
            Directory.CreateDirectory(outDir); // 应用中目录由对话框选定必然存在，测试先建好
            vm.OutputFolder = outDir;
            vm.StartTranscodeCommand.NotifyCanExecuteChanged(); // 无头环境没有跑动的 Dispatcher，手动刷新
            vm.StartTranscodeCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Check(!vm.IsBusy, "IsBusy stuck after transcode");
            Check(File.Exists(Path.Combine(outDir, "w0.mp4")), "w0.mp4");
            Check(File.Exists(Path.Combine(outDir, "w1.mp4")), "w1.mp4");
            Check(File.Exists(Path.Combine(outDir, "sub", "w2.mp4")), "sub/w2.mp4");
            Check(File.Exists(Path.Combine(outDir, "sub", "w3.mp4")), "sub/w3.mp4");
            vm.Dispose();
        });
    }

    private static void VmImageScanFilter(string workDir)
    {
        Run("vm-image-scan-filter", () =>
        {
            // 图片页共用 BatchFileScanner/AddRange 管线，这里验证扩展名过滤与批量入表
            string root = Path.Combine(workDir, "vm-image");
            Directory.CreateDirectory(root);
            for (int i = 0; i < 300; i++) File.WriteAllText(Path.Combine(root, $"p{i}.png"), "x");
            for (int i = 0; i < 50; i++) File.WriteAllText(Path.Combine(root, $"t{i}.txt"), "x"); // 应被过滤

            var vm = new ImageConvertViewModel(
                new FakeImageConversion(), new FakeSuperResolution(),
                new FakeDialogs(), new FakeNotifs(), new FakeViewState());
            vm.AddDroppedPaths([root]);
            WaitFor(() => vm.FileItems.Count == 300, 60_000, $"FileItems.Count={vm.FileItems.Count}");
            Check(vm.FileItems.All(i => Path.GetExtension(i.FilePath) == ".png"), "non-image leaked into list");
        });
    }

    // ══════════════ G. ViewModel 级：暂停/继续、终止、失败日志 ══════════════

    private static void VmAudioPauseResume(AudioConvertService svc, string workDir)
    {
        Run("vm-audio-pause-resume", () =>
        {
            string root = Path.Combine(workDir, "vm-pause");
            Directory.CreateDirectory(root);
            for (int i = 0; i < 24; i++) WriteSineWav(Path.Combine(root, $"p{i}.wav"), 44100, 2, 5.0);

            var vm = new AudioConvertViewModel(svc, new FakeFfmpeg(), new FakeDialogs(), new FakeNotifs(), new FakeViewState());
            vm.AddDroppedPaths([root]);
            WaitFor(() => vm.FileItems.Count == 24, 60_000, $"FileItems.Count={vm.FileItems.Count}");
            string outDir = Path.Combine(workDir, "vm-pause-out");
            Directory.CreateDirectory(outDir);
            vm.OutputFolder = outDir;
            vm.StartConvertCommand.NotifyCanExecuteChanged();

            var run = vm.StartConvertCommand.ExecuteAsync(null);
            int Count() => Directory.GetFiles(outDir, "*.mp3").Length;
            WaitFor(() => Count() >= 2, 60_000, "no early outputs");
            Check(Count() < 24, "batch finished before pause could engage");

            vm.TogglePauseCommand.Execute(null); // 暂停
            Check(vm.IsPaused, "IsPaused should be true");
            Thread.Sleep(900);                   // 等进行中的任务收尾
            int c1 = Count();
            Thread.Sleep(600);
            int c2 = Count();
            Check(c1 == c2, $"outputs grew while paused: {c1} -> {c2}");

            vm.TogglePauseCommand.Execute(null); // 继续
            Check(!vm.IsPaused, "IsPaused should be false");
            run.GetAwaiter().GetResult();
            Check(!vm.IsBusy, "IsBusy stuck");
            Check(Count() == 24, $"expected 24 outputs, got {Count()}");
            vm.Dispose();
        });
    }

    private static void VmAudioStop(AudioConvertService svc, string workDir)
    {
        Run("vm-audio-stop", () =>
        {
            string root = Path.Combine(workDir, "vm-stop");
            Directory.CreateDirectory(root);
            for (int i = 0; i < 12; i++) WriteSineWav(Path.Combine(root, $"s{i}.wav"), 44100, 2, 20.0);

            var vm = new AudioConvertViewModel(svc, new FakeFfmpeg(), new FakeDialogs(), new FakeNotifs(), new FakeViewState());
            vm.AddDroppedPaths([root]);
            WaitFor(() => vm.FileItems.Count == 12, 60_000, $"FileItems.Count={vm.FileItems.Count}");
            string outDir = Path.Combine(workDir, "vm-stop-out");
            Directory.CreateDirectory(outDir);
            vm.OutputFolder = outDir;
            vm.StartConvertCommand.NotifyCanExecuteChanged();

            var run = vm.StartConvertCommand.ExecuteAsync(null);
            int Count() => Directory.GetFiles(outDir, "*.mp3").Length;
            WaitFor(() => Count() >= 1, 120_000, "no output before stop");
            Check(Count() < 12, "batch finished before stop could engage");

            vm.StopConvertCommand.Execute(null); // 终止
            run.GetAwaiter().GetResult();
            Check(!vm.IsBusy, "IsBusy stuck after stop");
            int final = Count();
            Check(final is >= 1 and < 12, $"stop mid-batch output count out of range: {final}");
            Check(Directory.GetFiles(outDir, "*.tmp", SearchOption.AllDirectories).Length == 0, "tmp leftover after stop");
            // 已完成的输出必须可播
            foreach (string f in Directory.GetFiles(outDir, "*.mp3"))
            {
                var p = svc.ProbeAsync(f).GetAwaiter().GetResult();
                Check(p != null, "partial output not playable: " + f);
            }
            vm.Dispose();
        });
    }

    private static void VmAudioFailureLog(string workDir)
    {
        Run("vm-audio-failure-log", () =>
        {
            string root = Path.Combine(workDir, "vm-fail");
            Directory.CreateDirectory(root);
            for (int i = 0; i < 3; i++) WriteSineWav(Path.Combine(root, $"ok{i}.wav"), 44100, 1, 0.3);
            string corrupt = Path.Combine(root, "corrupt.wav");
            File.WriteAllText(corrupt, "this is not a wav file at all");

            var recorder = new RecordingLoggerProvider();
            using var factory = LoggerFactory.Create(b => b.AddProvider(recorder));
            var vm = new AudioConvertViewModel(
                new AudioConvertService(NullLogger<AudioConvertService>.Instance),
                new FakeFfmpeg(), new FakeDialogs(), new FakeNotifs(), new FakeViewState(),
                factory.CreateLogger<AudioConvertViewModel>());

            vm.AddDroppedPaths([root]);
            WaitFor(() => vm.FileItems.Count == 4, 60_000, $"FileItems.Count={vm.FileItems.Count}");
            string outDir = Path.Combine(workDir, "vm-fail-out");
            Directory.CreateDirectory(outDir);
            vm.OutputFolder = outDir;
            vm.StartConvertCommand.NotifyCanExecuteChanged();
            vm.StartConvertCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            int mp3 = Directory.GetFiles(outDir, "*.mp3").Length;
            Check(mp3 == 3, $"valid outputs: want 3 got {mp3}");

            var failEntry = recorder.Entries.FirstOrDefault(e =>
                e.Level == LogLevel.Error && e.Message.Contains("corrupt.wav"));
            Check(failEntry != default, "no Error log entry for corrupt.wav");
            Check(failEntry.Exception != null, "failure log entry lacks exception detail");
            Check(failEntry.Message.Contains("->"), $"log entry should include output path: {failEntry.Message}");
            Console.WriteLine($"    logged failure: {failEntry.Message} | ex: {failEntry.Exception?.Message.Split('\n')[0]}");
        });
    }

    private static void WaitFor(Func<bool> condition, int timeoutMs, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new InvalidOperationException($"timeout waiting for: {what}");
            Thread.Sleep(50);
        }
    }

    // ══════════════ 公共 ══════════════

    /// <summary>按 VM 的规划逻辑生成 (输入, 输出) 列表：镜像 + 批内/磁盘去重。</summary>
    private static List<(string File, string OutputPath)> BuildAudioPlan(
        string outputFolder, List<(string File, string? Root)> items, string ext, bool suffixWhenSameDir)
    {
        var taken = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<(string File, string OutputPath)>(items.Count);
        foreach ((string file, string? root) in items)
        {
            string sourceDir = Path.GetDirectoryName(file)!;
            string outputDir = OutputPathPlanner.ResolveOutputDir(outputFolder, file, root);
            bool sameDir = string.Equals(Path.GetFullPath(outputDir), Path.GetFullPath(sourceDir), StringComparison.OrdinalIgnoreCase);
            plan.Add((file, OutputPathPlanner.PlanUniquePath(outputDir,
                Path.GetFileNameWithoutExtension(file), ext, suffixWhenSameDir && sameDir, taken)));
        }
        return plan;
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
            _failedNames.Add(name);
            Console.WriteLine($"  [{name}] FAIL: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
    }

    private static void Check(bool cond, string message)
    {
        if (!cond) throw new InvalidOperationException(message);
    }

    /// <summary>直接写 RIFF PCM16 WAV（与 AudioSmokeTest 一致）。</summary>
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
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataLen);
        foreach (short s in samples) w.Write(s);
    }

    /// <summary>直写最小 AVI（rawvideo BGR24 + PCM16 正弦），与 FfmpegSmokeTest 相同的生成方式。</summary>
    private static void WriteTestAvi(string path, int width, int height, int fps, double seconds)
    {
        int frames = (int)(fps * seconds);
        int sampleRate = 44100;
        int totalAudioSamples = (int)(sampleRate * seconds);
        int stride = (width * 3 + 3) & ~3;
        int frameBytes = stride * height;

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
                    int row = height - 1 - y;
                    int off = row * stride + x * 3;
                    buf[off] = inBox ? (byte)255 : b;
                    buf[off + 1] = inBox ? (byte)200 : g;
                    buf[off + 2] = inBox ? (byte)0 : r;
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

        fourcc("LIST");
        w.Write(4 + 8 + 56 + 8 + 40 + 8 + 8 + 56 + 8 + 16);
        fourcc("hdrl");
        fourcc("avih");
        w.Write(56);
        w.Write((uint)(1_000_000 / fps));
        w.Write((uint)(frameBytes * fps + sampleRate * 2));
        w.Write(0u);
        w.Write(0x10u);
        w.Write((uint)frames);
        w.Write(0u);
        w.Write(2u);
        w.Write((uint)frameBytes);
        w.Write((uint)width);
        w.Write((uint)height);
        for (int i = 0; i < 4; i++) w.Write(0u);

        fourcc("LIST");
        w.Write(4 + 8 + 56 + 8 + 40);
        fourcc("strl");
        fourcc("strh");
        w.Write(56);
        fourcc("vids"); fourcc("DIB ");
        w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
        w.Write(0u);
        w.Write(1u); w.Write((uint)fps);
        w.Write(0u); w.Write((uint)frames);
        w.Write((uint)frameBytes);
        w.Write(0xFFFFFFFF);
        w.Write(0u);
        w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)width); w.Write((ushort)height);

        fourcc("strf");
        w.Write(40);
        w.Write(40);
        w.Write(width); w.Write(height);
        w.Write((ushort)1); w.Write((ushort)24);
        w.Write(0u);
        w.Write((uint)frameBytes);
        for (int i = 0; i < 4; i++) w.Write(0u);

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
        w.Write(2u);
        w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);

        fourcc("strf");
        w.Write(16);
        w.Write((ushort)1);
        w.Write((ushort)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((ushort)2);
        w.Write((ushort)16);

        fourcc("LIST");
        var moviSizePos = ms.Position; w.Write(0u);
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

        fourcc("idx1");
        w.Write((uint)(idx.Count * 16));
        foreach (var (id, off, len) in idx)
        {
            fourcc(id);
            w.Write(0x10u);
            w.Write(off);
            w.Write(len);
        }

        long end = ms.Position;
        ms.Position = riffSizePos; w.Write((uint)(end - 8));
        ms.Position = moviSizePos; w.Write(moviSize);
        ms.Position = end;

        File.WriteAllBytes(path, ms.ToArray());
    }

    // ══════════════ VM 测试替身 ══════════════

    private sealed record LogEntryCapture(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<LogEntryCapture> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Recorder(this);

        public void Dispose() { }

        private sealed class Recorder(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Entries.Add(new LogEntryCapture(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class FakeFfmpeg : IFfmpegService
    {
        public bool IsAvailable => true;
        public string? LastError => null;
        public IReadOnlyList<string> AvailableVideoEncoders => ["libx264"];
        public IReadOnlyList<string> AvailableAudioEncoders => ["mp3", "aac"];
        public string? VersionInfo => "fake";
        public Task<bool> ValidateAsync(CancellationToken ct = default) => Task.FromResult(true);
        public bool ValidateEncoder(string encoderName) => true;
        public event EventHandler<bool>? AvailabilityChanged { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class FakeNotifs : INotificationService
    {
        public void ShowError(string message) { }
        public void ShowSuccess(string message) { }
        public void ShowInfo(string message) { }
        public void ShowWarn(string message) { }
    }

    private sealed class FakeDialogs : IFileDialogService
    {
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FilePickerFileType>? filters = null)
            => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>?> PickOpenFilesAsync(string title, IReadOnlyList<FilePickerFileType>? filters = null)
            => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task<string?> PickSaveFileAsync(string title, string? defaultFileName = null, IReadOnlyList<FilePickerFileType>? filters = null)
            => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public void OpenInExplorer(string folderPath) { }
    }

    private sealed class FakeViewState : IViewStateService
    {
        public void Register(FancyToolAva.Models.ViewStates.IViewState view) { }
        public void Unregister(FancyToolAva.Models.ViewStates.IViewState view) { }
        public void SaveAll() { }
        public void LoadAll() { }
    }

    private sealed class FakeImageConversion : IImageConversionService
    {
        public Task<string?> ImageToBase64Async(string imagePath, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<Avalonia.Media.Imaging.Bitmap?> Base64ToBitmapAsync(string base64, CancellationToken ct = default) => Task.FromResult<Avalonia.Media.Imaging.Bitmap?>(null);
        public Task<string?> ConvertImageFormatAsync(string inputPath, string outputPath, string format,
            CancellationToken ct = default, int? maxDimension = null, string? filterType = null,
            IProgress<double>? progress = null, int? scalePercent = null,
            SuperResolutionModel? superResolutionModel = null, int superResolutionScale = 4,
            ISuperResolutionService? superResolutionService = null) => Task.FromResult<string?>(null);
        public Task<Avalonia.Media.Imaging.Bitmap?> LoadImageAsync(string imagePath, CancellationToken ct = default) => Task.FromResult<Avalonia.Media.Imaging.Bitmap?>(null);
        public Task<byte[]> ImageToBytesAsync(string imagePath, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> CreateIcoBytesAsync(string imagePath, int size, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<bool> SaveAsIcoAsync(string imagePath, string outputPath, int size, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class FakeSuperResolution : ISuperResolutionService
    {
        public VideoSrDevice? InferenceDevice => null;
        public bool PreferNpu { get; set; }
        public event Action<VideoSrDevice>? DeviceChanged { add { } remove { } }
        public IReadOnlyList<string> AvailableModels => [];
        public bool IsModelAvailable(SuperResolutionModel model) => false;
        public Task<SkiaSharp.SKBitmap> UpscaleAsync(SkiaSharp.SKBitmap source, SuperResolutionModel model,
            int targetScale, IProgress<double>? progress = null, CancellationToken ct = default) => throw new InvalidOperationException("unused");
        public void ReleaseSessions() { }
        public Task WarmupTask => Task.CompletedTask;
        public bool IsDownloading => false;
        public string? LastDownloadError => null;
        public event Action<ModelDownloadProgress>? DownloadProgressChanged { add { } remove { } }
        public event EventHandler<bool>? IsDownloadingChanged { add { } remove { } }
        public void Dispose() { }
    }
}
