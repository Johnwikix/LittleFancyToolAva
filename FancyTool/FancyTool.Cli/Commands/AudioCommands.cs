using FancyToolAva.Models;
using FancyToolAva.Services;
using FancyToolAva.Utils;

namespace FancyToolAva.Cli.Commands;

internal static class AudioCommands
{
    public static void Register(Dictionary<string, Func<CliContext, Task<int>>> map)
    {
        map["audio probe"] = ProbeAsync;
        map["audio convert"] = ConvertAsync;
    }

    private static async Task<int> ProbeAsync(CliContext ctx)
    {
        string input = ctx.Args.Require("--input", ctx.Zh ? "需要 -i/--input。" : "-i/--input is required.");
        if (!File.Exists(input)) throw new FileNotFoundException(input);
        var service = ctx.Get<IAudioConvertService>();
        AudioProbeInfo? probe = await service.ProbeAsync(input, ctx.CancellationToken).ConfigureAwait(false);
        if (probe is null) throw new InvalidOperationException("No audio stream was found.");
        if (ctx.Json)
        {
            ctx.WriteJson(new
            {
                filePath = probe.FilePath,
                codec = probe.Codec,
                sampleRate = probe.SampleRate,
                channels = probe.Channels,
                bitDepth = probe.BitDepth,
                bitRateKbps = probe.BitRateKbps,
                durationMs = probe.DurationMs,
                isDsd = probe.IsDsd,
            });
        }
        else
        {
            ctx.WriteResult(probe.Display);
        }
        return 0;
    }

    private static async Task<int> ConvertAsync(CliContext ctx)
    {
        IReadOnlyList<string> inputs = ctx.Args.GetAll("--input");
        if (inputs.Count == 0) throw new CliUsageException(ctx.Zh ? "需要 -i/--input。" : "-i/--input is required.");
        string? output = ctx.Args.Get("--out");
        string? outDir = ctx.Args.Get("--out-dir");
        if (output is null && outDir is null)
            throw new CliUsageException(ctx.Zh ? "需要 -o/--out 或 --out-dir。" : "-o/--out or --out-dir is required.");
        if (inputs.Count > 1 && outDir is null)
            throw new CliUsageException(ctx.Zh ? "批量处理需要 --out-dir。" : "Batch processing requires --out-dir.");
        if (inputs.Count > 1 && output is not null)
            throw new CliUsageException(ctx.Zh ? "多个输入不能同时使用 -o/--out。" : "-o/--out cannot be used with multiple inputs.");

        AudioConvertFormat format = ParseFormat(ctx, output);
        string extension = AudioConvertService.GetExtension(format);
        var options = new AudioConvertOptions
        {
            Format = format,
            BitrateKbps = ctx.Args.GetInt("--bitrate", 320),
            BitDepth = ctx.Args.GetChoice("--bit-depth", "auto", "auto", "s16", "s24", "s32", "f32") switch
            {
                "s16" => AudioBitDepthMode.S16,
                "s24" => AudioBitDepthMode.S24,
                "s32" => AudioBitDepthMode.S32,
                "f32" => AudioBitDepthMode.F32,
                _ => AudioBitDepthMode.Auto,
            },
            SampleRateHz = ctx.Args.GetInt("--sample-rate", 0),
            EnableSpectralEnhancement = ctx.Args.GetBool("--enhance"),
            UpsamplingMultiplier = ctx.Args.GetInt("--upsample", 2),
            UpsamplingBackendId = ctx.Args.Get("--upsampling-backend") ?? "managed-dsp",
            GainDb = ctx.Args.GetDouble("--gain-db", 0),
            CopyMetadata = !ctx.Args.GetBool("--no-metadata"),
        };

        var service = ctx.Get<IAudioConvertService>();
        var results = new List<object>();
        int failed = 0;
        foreach (string input in inputs)
        {
            if (!File.Exists(input))
            {
                failed++;
                ctx.Error((ctx.Zh ? "找不到文件: " : "File not found: ") + input);
                continue;
            }
            string target;
            if (output is not null)
            {
                target = output;
                if (File.Exists(target) && !ctx.Args.GetBool("--force"))
                {
                    failed++;
                    ctx.Error((ctx.Zh ? "输出文件已存在，使用 --force 覆盖: " : "Output exists; use --force to overwrite: ") + target);
                    continue;
                }
            }
            else
            {
                Directory.CreateDirectory(outDir!);
                target = OutputPathPlanner.PlanUniquePath(outDir!, Path.GetFileNameWithoutExtension(input), extension, false,
                    new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase));
            }

            try
            {
                var progress = ctx.Quiet ? null : new Progress<double>(p => ctx.Progress(p));
                await service.ConvertAsync(input, target, options, progress, ctx.CancellationToken).ConfigureAwait(false);
                ctx.ProgressDone();
                results.Add(new { input, output = target, format = format.ToString(), success = true, error = (string?)null });
                if (!ctx.Json) ctx.WriteResult(target);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                ctx.ProgressDone();
                ctx.Error((ctx.Zh ? "转换失败: " : "Conversion failed: ") + input + " - " + ex.Message);
                results.Add(new { input, output = target, format = format.ToString(), success = false, error = ex.Message });
            }
        }

        if (ctx.Json && inputs.Count > 1) ctx.WriteJson(new { failed, results });
        else if (ctx.Json && results.Count == 1) ctx.WriteJson(results[0]);
        return failed == 0 ? 0 : 1;
    }

    private static AudioConvertFormat ParseFormat(CliContext ctx, string? output)
    {
        string? value = ctx.Args.Get("--format");
        string format = (value ?? (output is null ? "mp3" : Path.GetExtension(output).TrimStart('.'))).ToLowerInvariant();
        return format switch
        {
            "mp3" => AudioConvertFormat.Mp3,
            "m4a" or "aac" => AudioConvertFormat.AacM4a,
            "flac" => AudioConvertFormat.Flac,
            "alac" => AudioConvertFormat.AlacM4a,
            "wav" => AudioConvertFormat.Wav,
            "aiff" or "aif" => AudioConvertFormat.Aiff,
            "ogg" or "vorbis" => AudioConvertFormat.OggVorbis,
            "opus" => AudioConvertFormat.Opus,
            "wma" => AudioConvertFormat.Wma,
            "ac3" => AudioConvertFormat.Ac3,
            "mp2" => AudioConvertFormat.Mp2,
            _ => throw new CliUsageException($"Unsupported audio format: {format}"),
        };
    }
}