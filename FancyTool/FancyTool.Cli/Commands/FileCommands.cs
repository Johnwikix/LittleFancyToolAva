using System.Text;
using System.Text.Json;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FancyToolAva.Utils;

namespace FancyToolAva.Cli.Commands;

internal static class FileCommands
{
    public static void Register(Dictionary<string, Func<CliContext, Task<int>>> map)
    {
        map["file encrypt"] = ctx => FileCryptoAsync(ctx, encrypt: true);
        map["file decrypt"] = ctx => FileCryptoAsync(ctx, encrypt: false);
        map["file keygen"] = KeygenAsync;
        map["file compare"] = CompareAsync;
    }

    private static async Task<int> KeygenAsync(CliContext ctx)
    {
        string keyType = ctx.Args.GetChoice("--key-type", "hex", "text", "base64", "hex")!;
        int keyLength = ctx.Args.GetInt("--key-length", 128);
        if (keyLength is not (128 or 192 or 256))
            throw new CliUsageException("--key-length must be 128, 192, or 256.");
        string key = ToolMethod.GenerateSymmetricKey(keyLength, keyType);
        string iv = ToolMethod.GenerateSymmetricKey(128, keyType);
        string json = System.Text.Json.JsonSerializer.Serialize(new { Key = key, Iv = iv, Format = keyType });
        string? path = ctx.Args.Get("--out");
        if (path is not null)
        {
            if (File.Exists(path) && !ctx.Args.GetBool("--force"))
                throw new CliUsageException((ctx.Zh ? "文件已存在，使用 --force 覆盖: " : "File exists; use --force to overwrite: ") + path);
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, json, ctx.CancellationToken).ConfigureAwait(false);
        }
        if (ctx.Json || path is null)
            ctx.WriteResult(json);
        else
            ctx.WriteResult(path);
        return 0;
    }

    private static async Task<int> FileCryptoAsync(CliContext ctx, bool encrypt)
    {
        IReadOnlyList<string> inputs = ctx.Args.GetAll("--input");
        if (inputs.Count == 0) throw new CliUsageException(ctx.Zh ? "需要 -i/--input。" : "-i/--input is required.");
        string? explicitOutput = ctx.Args.Get("--out");
        string? outputDirectory = ctx.Args.Get("--out-dir");
        if (inputs.Count > 1 && outputDirectory is null)
            throw new CliUsageException(ctx.Zh ? "批量处理需要 --out-dir。" : "Batch processing requires --out-dir.");
        if (inputs.Count > 1 && explicitOutput is not null)
            throw new CliUsageException(ctx.Zh ? "多个输入不能同时使用 -o/--out。" : "-o/--out cannot be used with multiple inputs.");

        string keyType = ctx.Args.GetChoice("--key-type", "hex", "text", "base64", "hex")!;
        int keyLength = ctx.Args.GetInt("--key-length", 128);
        if (keyLength is not (128 or 192 or 256))
            throw new CliUsageException("--key-length must be 128, 192, or 256.");
        string? key = ctx.Args.Get("--key");
        string? iv = ctx.Args.Get("--iv");
        if (ctx.Args.Get("--key-file") is { } keyFile)
        {
            (key, iv, keyType) = ReadKeyFile(keyFile);
            if (ctx.Args.Get("--key-type") is not null)
                keyType = ctx.Args.GetChoice("--key-type", keyType, "text", "base64", "hex")!;
        }
        bool generatedKey = false, generatedIv = false;
        if (string.IsNullOrEmpty(key))
        {
            if (!encrypt) throw new CliUsageException(ctx.Zh ? "解密需要 -k/--key 或 --key-file。" : "Decryption requires -k/--key or --key-file.");
            key = ToolMethod.GenerateSymmetricKey(keyLength, keyType);
            generatedKey = true;
        }
        if (string.IsNullOrEmpty(iv))
        {
            if (!encrypt) throw new CliUsageException(ctx.Zh ? "解密需要 --iv。" : "Decryption requires --iv.");
            iv = ToolMethod.GenerateSymmetricKey(128, keyType);
            generatedIv = true;
        }

        var service = ctx.Get<IFileEncryptionService>();
        var results = new List<object>();
        int failed = 0;
        for (int index = 0; index < inputs.Count; index++)
        {
            string input = inputs[index];
            if (!File.Exists(input))
            {
                failed++;
                ctx.Error((ctx.Zh ? "找不到文件: " : "File not found: ") + input);
                continue;
            }
            string output = explicitOutput ?? UniquePath(Path.Combine(
                outputDirectory ?? Path.GetDirectoryName(input) ?? ".",
                Path.GetFileName(DefaultOutputPath(input, encrypt))));
            if (explicitOutput is not null) EnsureWritable(ctx, output);
            if (Path.GetDirectoryName(output) is { Length: > 0 } outputParent) Directory.CreateDirectory(outputParent);
            var progress = ctx.Quiet ? null : new Progress<double>(p => ctx.Progress(p));
            try
            {
                if (encrypt)
                    await service.EncryptFileAsync(input, output, key, iv, progress, ctx.CancellationToken, keyType).ConfigureAwait(false);
                else
                    await service.DecryptFileAsync(input, output, key, iv, progress, ctx.CancellationToken, keyType).ConfigureAwait(false);
                ctx.ProgressDone();
                results.Add(new { input, output, success = true, error = (string?)null });
                if (!ctx.Json) ctx.WriteResult(output);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                ctx.ProgressDone();
                ctx.Error((ctx.Zh ? "处理失败: " : "Failed: ") + input + " - " + ex.Message);
                results.Add(new { input, output, success = false, error = ex.Message });
            }
        }

        if (generatedKey || generatedIv)
            ctx.Info((ctx.Zh ? "已自动生成密钥/IV: " : "Generated key/IV: ")
                     + $"key={key} iv={iv} key-type={keyType}");
        if (ctx.Json && inputs.Count > 1)
            ctx.WriteJson(new { failed, results });
        else if (ctx.Json && results.Count == 1)
            ctx.WriteJson(results[0]);
        return failed == 0 ? 0 : 1;
    }

    private static (string Key, string Iv, string Format) ReadKeyFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        string Read(string name)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value.GetString() ?? string.Empty;
            }
            return string.Empty;
        }
        string key = Read("Key");
        string iv = Read("Iv");
        string format = Read("Format");
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(iv))
            throw new CliUsageException("Key file must contain Key and Iv.");
        if (format is not ("text" or "base64" or "hex")) format = "hex";
        return (key, iv, format);
    }

    private static async Task<int> CompareAsync(CliContext ctx)
    {
        string source = ctx.Args.Require("--source", ctx.Zh ? "需要 -s/--source。" : "-s/--source is required.");
        string target = ctx.Args.Require("--target", ctx.Zh ? "需要 -d/--target。" : "-d/--target is required.");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);
        bool useHash = ctx.Args.GetBool("--hash");
        bool useMusic = ctx.Args.GetBool("--music-title");
        var service = ctx.Get<IFolderCompareService>();
        var progress = ctx.Quiet ? null : new Progress<double>(p => ctx.Progress(p));
        List<FolderCompareResult> results = await service.CompareFoldersAsync(
            source, target, useHash, useMusic, progress, ctx.CancellationToken).ConfigureAwait(false);
        ctx.ProgressDone();

        results.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        if (ctx.Json)
        {
            ctx.WriteJson(results.Select(r => new
            {
                relativePath = r.RelativePath,
                state = r.State.ToString(),
                sourceDetail = r.SourceDetail,
                targetDetail = r.TargetDetail,
            }));
        }
        else
        {
            var sb = new StringBuilder();
            foreach (IGrouping<CompareState, FolderCompareResult> group in results.GroupBy(r => r.State))
                sb.AppendLine($"{group.Key}: {group.Count()}");
            sb.AppendLine();
            foreach (FolderCompareResult result in results.Where(r => r.State != CompareState.Match))
            {
                sb.Append(result.State).Append("  ").Append(result.RelativePath);
                if (!string.IsNullOrEmpty(result.SourceDetail)) sb.Append("  <- ").Append(result.SourceDetail);
                if (!string.IsNullOrEmpty(result.TargetDetail)) sb.Append("  -> ").Append(result.TargetDetail);
                sb.AppendLine();
            }
            ctx.WriteResult(sb.ToString());
        }

        if (ctx.Args.Get("--csv") is { } csvPath)
        {
            var csv = new StringBuilder("relativePath,state,sourceDetail,targetDetail\n");
            foreach (FolderCompareResult result in results)
                csv.Append(Csv(result.RelativePath)).Append(',').Append(result.State).Append(',')
                    .Append(Csv(result.SourceDetail ?? string.Empty)).Append(',')
                    .Append(Csv(result.TargetDetail ?? string.Empty)).Append('\n');
            await File.WriteAllTextAsync(csvPath, csv.ToString(), new UTF8Encoding(false), ctx.CancellationToken).ConfigureAwait(false);
            ctx.Info((ctx.Zh ? "CSV 已写入 " : "CSV written to ") + csvPath);
        }
        return 0;
    }

    private static string DefaultOutputPath(string input, bool encrypt)
    {
        if (encrypt) return input + ".enc";
        return input.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)
            ? input[..^4]
            : input + ".dec";
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

    private static void EnsureWritable(CliContext ctx, string path)
    {
        if (File.Exists(path) && !ctx.Args.GetBool("--force"))
            throw new CliUsageException((ctx.Zh ? "输出文件已存在，使用 --force 覆盖: " : "Output exists; use --force to overwrite: ") + path);
    }

    private static string Csv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r')) return value;
        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}