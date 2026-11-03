using FancyToolAva.Services;

namespace FancyToolAva.Cli.Commands;

internal static class MiscCommands
{
    public static void Register(Dictionary<string, Func<CliContext, Task<int>>> map)
    {
        map["ffmpeg info"] = FfmpegInfoAsync;
        map["skill install"] = ctx => SkillAsync(ctx, install: true);
        map["skill print"] = SkillPrintAsync;
        map["skill uninstall"] = ctx => SkillAsync(ctx, install: false);
    }

    private static async Task<int> FfmpegInfoAsync(CliContext ctx)
    {
        var service = ctx.Get<IFfmpegService>();
        bool available = await service.ValidateAsync(ctx.CancellationToken).ConfigureAwait(false);
        if (ctx.Json)
        {
            ctx.WriteJson(new
            {
                available,
                error = service.LastError,
                version = service.VersionInfo,
                videoEncoders = service.AvailableVideoEncoders,
                audioEncoders = service.AvailableAudioEncoders,
            });
        }
        else
        {
            ctx.WriteResult(
                $"{(ctx.Zh ? "可用" : "Available")}: {available}\n" +
                $"{(ctx.Zh ? "版本" : "Version")}: {service.VersionInfo ?? "-"}\n" +
                $"{(ctx.Zh ? "视频编码器" : "Video encoders")}: {string.Join(", ", service.AvailableVideoEncoders)}\n" +
                $"{(ctx.Zh ? "音频编码器" : "Audio encoders")}: {string.Join(", ", service.AvailableAudioEncoders)}" +
                (available ? string.Empty : $"\n{service.LastError}"));
        }
        return available ? 0 : 1;
    }

    private static async Task<int> SkillAsync(CliContext ctx, bool install)
    {
        SkillInstallTarget target = ParseTarget(ctx);
        IReadOnlyList<SkillInstallResult> results = install
            ? await SkillInstaller.InstallAsync(target, ctx.Zh, ctx.Args.GetBool("--force"), ctx.CancellationToken).ConfigureAwait(false)
            : await SkillInstaller.UninstallAsync(target, ctx.Zh, ctx.CancellationToken).ConfigureAwait(false);
        if (ctx.Json)
        {
            ctx.WriteJson(results.Select(r => new { target = r.Target, path = r.Path, changed = r.Changed, message = r.Message }));
        }
        else
        {
            foreach (SkillInstallResult result in results)
                ctx.WriteResult($"{result.Target}: {(result.Changed ? (ctx.Zh ? "已更新" : "updated") : (ctx.Zh ? "未更改" : "unchanged"))} {result.Path} - {result.Message}");
        }
        return 0;
    }

    private static async Task<int> SkillPrintAsync(CliContext ctx)
    {
        string content = CommandCatalog.RenderSkillMarkdown(ctx.Zh);
        await ctx.WriteOutputAsync(content, null).ConfigureAwait(false);
        return 0;
    }

    private static SkillInstallTarget ParseTarget(CliContext ctx) =>
        ctx.Args.GetChoice("--target", "qoder", "qoder", "agents", "both") switch
        {
            "agents" => SkillInstallTarget.AgentsFile,
            "both" => SkillInstallTarget.Both,
            _ => SkillInstallTarget.QoderSkill,
        };
}