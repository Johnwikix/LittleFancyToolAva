using System.Security.Cryptography;
using System.Text;
using FancyToolAva.Algorithms;
using FancyToolAva.Algorithms.Encryption;
using FancyToolAva.Utils;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Utilities.Encoders;

namespace FancyToolAva.Cli.Commands;

internal static class CryptoCommands
{
    public static void Register(Dictionary<string, Func<CliContext, Task<int>>> map)
    {
        foreach (string group in new[] { "aes", "des", "sm4" })
        {
            map[$"{group} keygen"] = ctx => SymmetricKeygenAsync(ctx, group);
            map[$"{group} encrypt"] = ctx => SymmetricAsync(ctx, group, encrypt: true);
            map[$"{group} decrypt"] = ctx => SymmetricAsync(ctx, group, encrypt: false);
        }

        foreach (string algorithm in new[] { "md5", "sha1", "sha256", "sha384", "sha512", "sm3" })
        {
            string captured = algorithm;
            map[$"hash {captured}"] = ctx => HashAsync(ctx, captured);
        }

        map["base64 encode"] = Base64EncodeAsync;
        map["base64 decode"] = Base64DecodeAsync;
    }

    private static IEncryptionSymmetric CreateSymmetric(string group) => group switch
    {
        "aes" => new AESEncryption(),
        "des" => new DESEncryption(),
        "sm4" => new SM4Encryption(),
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    private static int DefaultKeyLength(string group) => group switch
    {
        "aes" => 128,
        "des" => 64,
        "sm4" => 128,
        _ => 128,
    };

    private static async Task<int> SymmetricKeygenAsync(CliContext ctx, string group)
    {
        IEncryptionSymmetric cipher = CreateSymmetric(group);
        string keyType = ctx.Args.GetChoice("--key-type", "text", "text", "base64", "hex")!;
        int keyLength = ctx.Args.GetInt("--key-length", DefaultKeyLength(group));
        ValidateKeyLength(group, keyLength);
        string key = ToolMethod.GenerateSymmetricKey(keyLength, keyType);
        string iv = ToolMethod.GenerateSymmetricKey(cipher.IvBitLength, keyType);
        if (ctx.Json)
        {
            ctx.WriteJson(new { algorithm = group.ToUpperInvariant(), keyLength, keyType, key, iv });
        }
        else
        {
            ctx.WriteResult($"Key: {key}\nIV: {iv}");
        }
        await Task.CompletedTask;
        return 0;
    }

    private static async Task<int> SymmetricAsync(CliContext ctx, string group, bool encrypt)
    {
        IEncryptionSymmetric cipher = CreateSymmetric(group);
        string input = (await ctx.ReadTextAsync().ConfigureAwait(false))!;
        string keyType = ctx.Args.GetChoice("--key-type", "text", "text", "base64", "hex")!;
        int keyLength = ctx.Args.GetInt("--key-length", DefaultKeyLength(group));
        ValidateKeyLength(group, keyLength);
        string mode = ctx.Args.GetChoice("--mode", "CBC", "ECB", "CBC")!;
        string padding = ctx.Args.GetChoice("--padding", "PKCS7",
            group == "sm4" ? ["PKCS7", "ISO10126", "ZEROBYTE"] : ["PKCS7", "Zeros", "None"])!;
        string outputFormat = ctx.Args.GetChoice("--output-format", "base64", "base64", "hex")!;

        string? key = ctx.Args.Get("--key");
        string? iv = ctx.Args.Get("--iv");
        bool generatedKey = false;
        bool generatedIv = false;
        if (string.IsNullOrEmpty(key))
        {
            if (!encrypt) throw new CliUsageException(ctx.Zh ? "解密需要 -k/--key。" : "Decryption requires -k/--key.");
            key = ToolMethod.GenerateSymmetricKey(keyLength, keyType);
            generatedKey = true;
        }
        if (string.IsNullOrEmpty(iv))
        {
            // SM4 实现无条件要求 IV；AES/DES 仅在 CBC 解密时强制。
            bool ivRequired = !encrypt && (mode == "CBC" || group == "sm4");
            if (ivRequired) throw new CliUsageException(ctx.Zh ? "解密 CBC/SM4 密文需要 --iv。" : "CBC/SM4 decryption requires --iv.");
            iv = ToolMethod.GenerateSymmetricKey(cipher.IvBitLength, keyType);
            generatedIv = true;
        }

        string result = encrypt
            ? cipher.Encrypt(input, key, padding, keyLength, iv, mode, outputFormat, keyType)
            : cipher.Decrypt(input, key, padding, keyLength, iv, mode, outputFormat, keyType);

        if (generatedKey || generatedIv)
            ctx.Info((ctx.Zh ? "已自动生成密钥/IV: " : "Generated key/IV: ")
                     + $"key={key} iv={iv} key-type={keyType}");

        string? outputPath = ctx.Args.Get("--out");
        if (ctx.Json)
        {
            ctx.WriteJson(new
            {
                result,
                key = generatedKey ? key : null,
                iv = generatedIv ? iv : null,
                keyType,
                mode,
                padding,
                outputFormat,
            });
        }
        else
        {
            await ctx.WriteOutputAsync(result, outputPath).ConfigureAwait(false);
        }
        return 0;
    }

    private static void ValidateKeyLength(string group, int keyLength)
    {
        bool valid = group switch
        {
            "aes" => keyLength is 128 or 192 or 256,
            "des" => keyLength == 64,
            "sm4" => keyLength == 128,
            _ => false,
        };
        if (!valid) throw new CliUsageException($"Unsupported {group.ToUpperInvariant()} key length: {keyLength}");
    }

    private static async Task<int> HashAsync(CliContext ctx, string algorithm)
    {
        bool lower = ctx.Args.GetBool("--lower");
        string text;
        bool isFile = ctx.Args.Get("--input") is not null && !ctx.Args.Has("--text") && !ctx.Args.Has("--stdin");
        if (isFile)
        {
            string file = ctx.Args.Get("--input")!;
            if (!File.Exists(file)) throw new FileNotFoundException(file);
            text = await Task.Run(() => HashFile(file, algorithm)).ConfigureAwait(false);
        }
        else
        {
            string input = (await ctx.ReadTextAsync().ConfigureAwait(false))!;
            text = HashText(input, algorithm);
        }

        if (algorithm == "md5")
        {
            int length = ctx.Args.GetInt("--length", 32);
            if (length is not (16 or 32)) throw new CliUsageException("--length must be 16 or 32 for MD5.");
            if (length == 16 && text.Length >= 24) text = text.Substring(8, 16);
        }
        text = lower ? text.ToLowerInvariant() : text.ToUpperInvariant();

        if (ctx.Json) ctx.WriteJson(new { algorithm, hash = text });
        else await ctx.WriteOutputAsync(text, ctx.Args.Get("--out")).ConfigureAwait(false);
        return 0;
    }

    private static string HashText(string input, string algorithm) => algorithm switch
    {
        "md5" => new Md5Encryption().Encrypt(input, "UPPER", 32),
        "sm3" => new SM3Encryption().Encrypt(input, "UPPER", 0, null),
        _ => new SHAEncrpytion().Encrypt(input, "UPPER", 0, algorithm.ToUpperInvariant()),
    };

    private static string HashFile(string path, string algorithm)
    {
        if (algorithm == "sm3")
        {
            using FileStream stream = File.OpenRead(path);
            var digest = new SM3Digest();
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                digest.BlockUpdate(buffer, 0, read);
            byte[] hash = new byte[digest.GetDigestSize()];
            digest.DoFinal(hash, 0);
            return Hex.ToHexString(hash);
        }
        string mode = algorithm.ToUpperInvariant();
        return ToolMethod.CalculateFileHash(path, mode);
    }

    private static async Task<int> Base64EncodeAsync(CliContext ctx)
    {
        byte[] bytes = (await ctx.ReadBytesAsync().ConfigureAwait(false))!;
        string result = Convert.ToBase64String(bytes);
        if (ctx.Json && ctx.Args.Get("--out") is null) ctx.WriteJson(new { base64 = result });
        else await ctx.WriteOutputAsync(result, ctx.Args.Get("--out")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> Base64DecodeAsync(CliContext ctx)
    {
        string input = (await ctx.ReadTextAsync().ConfigureAwait(false))!;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(RemoveWhitespace(input)); }
        catch (FormatException) { throw new CliUsageException(ctx.Zh ? "输入不是有效的 Base64。" : "Input is not valid Base64."); }
        await ctx.WriteOutputAsync(bytes, ctx.Args.Get("--out")).ConfigureAwait(false);
        return 0;
    }

    private static string RemoveWhitespace(string text) =>
        new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
}