using FancyToolAva.Algorithms;
using FancyToolAva.Algorithms.Encryption;

namespace FancyToolAva.Cli.Commands;

internal static class AsymmetricCommands
{
    private static readonly RSAEncryption Rsa = new();
    private static readonly SM2Encryption Sm2 = new();

    public static void Register(Dictionary<string, Func<CliContext, Task<int>>> map)
    {
        map["rsa keygen"] = RsaKeygenAsync;
        map["rsa encrypt"] = ctx => RsaTextAsync(ctx, encrypt: true);
        map["rsa decrypt"] = ctx => RsaTextAsync(ctx, encrypt: false);
        map["rsa file-encrypt"] = ctx => RsaFileAsync(ctx, encrypt: true);
        map["rsa file-decrypt"] = ctx => RsaFileAsync(ctx, encrypt: false);
        map["sm2 keygen"] = Sm2KeygenAsync;
        map["sm2 encrypt"] = ctx => Sm2TextAsync(ctx, encrypt: true);
        map["sm2 decrypt"] = ctx => Sm2TextAsync(ctx, encrypt: false);
    }

    private static async Task<int> RsaKeygenAsync(CliContext ctx)
    {
        int keyLength = ctx.Args.GetInt("--key-length", 2048);
        if (keyLength is not (1024 or 2048 or 4096))
            throw new CliUsageException("RSA key length must be 1024, 2048, or 4096.");
        string format = ctx.Args.GetChoice("--format", "PKCS#1", "PKCS#1", "PKCS#8")!;
        (string publicKey, string privateKey) = Rsa.GenerateKeyPair(keyLength, format);
        string? outDir = ctx.Args.Get("--out-dir");
        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            string prefix = ctx.Args.Get("--prefix") ?? "rsa";
            string publicPath = Path.Combine(outDir, prefix + "-public.pem");
            string privatePath = Path.Combine(outDir, prefix + "-private.pem");
            EnsureWritable(ctx, publicPath);
            EnsureWritable(ctx, privatePath);
            await File.WriteAllTextAsync(publicPath, publicKey, ctx.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(privatePath, privateKey, ctx.CancellationToken).ConfigureAwait(false);
            if (ctx.Json)
                ctx.WriteJson(new { algorithm = "RSA", keyLength, format, publicKeyPath = publicPath, privateKeyPath = privatePath });
            else
                ctx.WriteResult($"Public: {publicPath}\nPrivate: {privatePath}");
            return 0;
        }

        if (ctx.Json)
            ctx.WriteJson(new { algorithm = "RSA", keyLength, format, publicKey, privateKey });
        else
            ctx.WriteResult($"----- PUBLIC KEY -----\n{publicKey}\n----- PRIVATE KEY -----\n{privateKey}");
        return 0;
    }

    private static async Task<int> RsaTextAsync(CliContext ctx, bool encrypt)
    {
        string input = (await ctx.ReadTextAsync().ConfigureAwait(false))!;
        string key = await ReadKeyTextAsync(ctx).ConfigureAwait(false);
        string padding = ctx.Args.GetChoice("--padding", "Pkcs1",
            "Pkcs1", "OaepSHA1", "OaepSHA256", "OaepSHA384", "OaepSHA512")!;
        int keyLength = ctx.Args.GetInt("--key-length", 2048);
        string result = encrypt
            ? Rsa.Encrypt(input, key, padding, keyLength)
            : Rsa.Decrypt(input, key, padding, keyLength);
        if (ctx.Json) ctx.WriteJson(new { result });
        else await ctx.WriteOutputAsync(result, ctx.Args.Get("--out")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RsaFileAsync(CliContext ctx, bool encrypt)
    {
        string input = ctx.Args.Require("--input", ctx.Zh ? "需要 -i/--input。" : "-i/--input is required.");
        string output = ctx.Args.Require("--out", ctx.Zh ? "需要 -o/--out。" : "-o/--out is required.");
        if (!File.Exists(input)) throw new FileNotFoundException(input);
        EnsureWritable(ctx, output);
        string key = await ReadKeyTextAsync(ctx).ConfigureAwait(false);
        int keyLength = ctx.Args.GetInt("--key-length", 2048);
        if (encrypt) await Rsa.EncryptFileAsync(input, output, key, keyLength).ConfigureAwait(false);
        else await Rsa.DecryptFileAsync(input, output, key, keyLength).ConfigureAwait(false);
        ctx.ProgressDone();
        if (ctx.Json) ctx.WriteJson(new { input, output, bytes = new FileInfo(output).Length });
        else ctx.WriteResult(output);
        return 0;
    }

    private static async Task<int> Sm2KeygenAsync(CliContext ctx)
    {
        (string publicKey, string privateKey) = Sm2.GenerateKeyPair();
        string? outDir = ctx.Args.Get("--out-dir");
        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            string prefix = ctx.Args.Get("--prefix") ?? "sm2";
            string publicPath = Path.Combine(outDir, prefix + "-public.txt");
            string privatePath = Path.Combine(outDir, prefix + "-private.txt");
            EnsureWritable(ctx, publicPath);
            EnsureWritable(ctx, privatePath);
            await File.WriteAllTextAsync(publicPath, publicKey, ctx.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(privatePath, privateKey, ctx.CancellationToken).ConfigureAwait(false);
            if (ctx.Json)
                ctx.WriteJson(new { algorithm = "SM2", publicKeyPath = publicPath, privateKeyPath = privatePath });
            else
                ctx.WriteResult($"Public: {publicPath}\nPrivate: {privatePath}");
            return 0;
        }
        if (ctx.Json) ctx.WriteJson(new { algorithm = "SM2", publicKey, privateKey });
        else ctx.WriteResult($"Public: {publicKey}\nPrivate: {privateKey}");
        return 0;
    }

    private static async Task<int> Sm2TextAsync(CliContext ctx, bool encrypt)
    {
        string input = (await ctx.ReadTextAsync().ConfigureAwait(false))!;
        string key = await ReadKeyTextAsync(ctx).ConfigureAwait(false);
        string mode = ctx.Args.GetChoice("--mode", "C1C2C3", "C1C2C3", "C1C3C2")!;
        string result = encrypt ? Sm2.Encrypt(input, key, mode) : Sm2.Decrypt(input, key, mode);
        if (ctx.Json) ctx.WriteJson(new { result });
        else await ctx.WriteOutputAsync(result, ctx.Args.Get("--out")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<string> ReadKeyTextAsync(CliContext ctx)
    {
        string? value = ctx.Args.Get("--key-value") ?? ctx.Args.Get("--key");
        if (string.IsNullOrWhiteSpace(value))
            throw new CliUsageException(ctx.Zh ? "需要 -k/--key 或 --key-value。" : "Provide -k/--key or --key-value.");
        if (File.Exists(value))
            return await File.ReadAllTextAsync(value, ctx.CancellationToken).ConfigureAwait(false);
        return value;
    }

    private static void EnsureWritable(CliContext ctx, string path)
    {
        if (File.Exists(path) && !ctx.Args.GetBool("--force"))
            throw new CliUsageException((ctx.Zh ? "文件已存在，使用 --force 覆盖: " : "File already exists; use --force to overwrite: ") + path);
    }
}