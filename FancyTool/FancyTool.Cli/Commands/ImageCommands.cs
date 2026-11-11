using FancyToolAva.Services;

namespace FancyToolAva.Cli.Commands;

internal static class ImageCommands
{
    public static void Register(Dictionary<string, Func<CliContext, Task<int>>> map)
    {
        map["image base64"] = ImageToBase64Async;
        map["image from-base64"] = ImageFromBase64Async;
        map["image convert"] = ConvertAsync;
    }

    private static async Task<int> ImageToBase64Async(CliContext ctx)
    {
        string input = ctx.Args.Require("--input", ctx.Zh ? "需要 -i/--input。" : "-i/--input is required.");
        if (!File.Exists(input)) throw new FileNotFoundException(input);
        var service = ctx.Get<IImageConversionService>();
        string? result = await service.ImageToBase64Async(input, ctx.CancellationToken).ConfigureAwait(false);
        if (result is null) throw new InvalidOperationException("Image could not be read.");
        if (ctx.Json && ctx.Args.Get("--out") is null) ctx.WriteJson(new { base64 = result });
        else await ctx.WriteOutputAsync(result, ctx.Args.Get("--out")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ImageFromBase64Async(CliContext ctx)
    {
        string base64 = (await ctx.ReadTextAsync().ConfigureAwait(false))!;
        var service = ctx.Get<IImageConversionService>();
        byte[]? png = await service.Base64ToPreviewPngAsync(RemoveWhitespace(base64), ctx.CancellationToken).ConfigureAwait(false);
        if (png is null) throw new InvalidOperationException("Base64 payload is not a decodable image.");
        string? output = ctx.Args.Get("--out");
        if (output is null && !ctx.Json)
            throw new CliUsageException(ctx.Zh ? "需要 -o/--out 指定 PNG 输出路径。" : "-o/--out is required for PNG output.");
        await ctx.WriteOutputAsync(png, output).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ConvertAsync(CliContext ctx)
    {
        IReadOnlyList<string> inputs = ctx.Args.GetAll("--input");
        if (inputs.Count == 0) throw new CliUsageException(ctx.Zh ? "需要 -i/--input。" : "-i/--input is required.");
        string? output = ctx.Args.Get("--out");
        string? outputDirectory = ctx.Args.Get("--out-dir");
        if (inputs.Count > 1 && outputDirectory is null)
            throw new CliUsageException(ctx.Zh ? "批量处理需要 --out-dir。" : "Batch processing requires --out-dir.");
        if (inputs.Count > 1 && output is not null)
            throw new CliUsageException(ctx.Zh ? "多个输入不能同时使用 -o/--out。" : "-o/--out cannot be used with multiple inputs.");

        string? formatOption = ctx.Args.Get("--format");
        int? maxDimension = ctx.Args.GetNullableInt("--max-dimension");
        int? scalePercent = ctx.Args.GetNullableInt("--scale-percent");
        string? filter = ctx.Args.Get("--filter");
        SuperResolutionModel? srModel = ParseSrModel(ctx);
        int srScale = ctx.Args.GetInt("--sr-scale", 4);
        if (srScale is not (2 or 4)) throw new CliUsageException("--sr-scale must be 2 or 4.");
        int icoSize = ctx.Args.GetInt("--ico-size", 256);

        ISuperResolutionService? srService = null;
        if (srModel.HasValue)
        {
            srService = ctx.Get<ISuperResolutionService>();
            srService.PreferNpu = ctx.Args.GetBool("--sr-use-npu");
        }
        var service = ctx.Get<IImageConversionService>();
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
            string format = (formatOption ?? (output is null ? "png" : Path.GetExtension(output).TrimStart('.'))).ToLowerInvariant();
            if (format is not ("jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "heic" or "ico"))
            {
                failed++;
                ctx.Error($"Unsupported image format: {format}");
                continue;
            }
            string target;
            if (output is not null)
            {
                target = output;
                if (Path.GetDirectoryName(target) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
            }
            else
            {
                Directory.CreateDirectory(outputDirectory!);
                string stem = Path.GetFileNameWithoutExtension(input);
                target = UniquePath(Path.Combine(outputDirectory!, stem + "." + (format == "jpeg" ? "jpg" : format)));
            }
            if (output is not null && File.Exists(target) && !ctx.Args.GetBool("--force"))
            {
                failed++;
                ctx.Error((ctx.Zh ? "输出文件已存在，使用 --force 覆盖: " : "Output exists; use --force to overwrite: ") + target);
                continue;
            }

            try
            {
                if (format == "ico")
                {
                    bool ok = await service.SaveAsIcoAsync(input, target, icoSize, ctx.CancellationToken).ConfigureAwait(false);
                    if (!ok) throw new InvalidOperationException("ICO encoding failed.");
                }
                else
                {
                    var progress = ctx.Quiet ? null : new Progress<double>(p => ctx.Progress(p));
                    await service.ConvertImageFormatAsync(
                        input, target, format, ctx.CancellationToken, maxDimension, filter, progress,
                        scalePercent, srModel, srScale, srService).ConfigureAwait(false);
                    ctx.ProgressDone();
                }
                results.Add(new { input, output = target, format, success = true, error = (string?)null });
                if (!ctx.Json) ctx.WriteResult(target);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                ctx.ProgressDone();
                ctx.Error((ctx.Zh ? "转换失败: " : "Conversion failed: ") + input + " - " + ex.Message);
                results.Add(new { input, output = target, format, success = false, error = ex.Message });
            }
        }

        if (ctx.Json && inputs.Count > 1) ctx.WriteJson(new { failed, results });
        else if (ctx.Json && results.Count == 1) ctx.WriteJson(results[0]);
        return failed == 0 ? 0 : 1;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string directory = Path.GetDirectoryName(path) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(directory, $"{stem}_{i}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static SuperResolutionModel? ParseSrModel(CliContext ctx)
    {
        string? value = ctx.Args.Get("--sr-model");
        if (value is null) return null;
        return value.ToLowerInvariant() switch
        {
            "realesrgan-x4plus" or "realesrgan-x4plus-fp16" => SuperResolutionModel.RealEsrganX4Plus,
            "realesrgan-x4plus-anime" => SuperResolutionModel.RealEsrganX4PlusAnime,
            "realesr-general-x4v3" or "realesrgan-general-x4v3" => SuperResolutionModel.RealEsrganGeneralX4V3,
            "clearreality" or "span-clearreality" or "4x-clearrealityv1" => SuperResolutionModel.SpanClearRealityV1,
            "vosr" or "vosr-1.4b" or "vosr14b" => SuperResolutionModel.Vosr1_4B,
            _ => throw new CliUsageException($"Unknown --sr-model: {value}"),
        };
    }

    private static string RemoveWhitespace(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
}