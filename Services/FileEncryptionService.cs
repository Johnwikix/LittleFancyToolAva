using LittleFancyToolAva.Algorithms.Encryption;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Utilities.Encoders;
using System.Security.Cryptography;
using System.Text;

namespace LittleFancyToolAva.Services;

public class FileEncryptionService : IFileEncryptionService
{
    private const int BufferSize = 1024 * 1024;
    private readonly ILogger<FileEncryptionService> _logger;

    public FileEncryptionService(ILogger<FileEncryptionService> logger)
    {
        _logger = logger;
    }

    public async Task EncryptFileAsync(string inputFilePath, string outputFilePath, string key, string iv, IProgress<double>? progress = null, CancellationToken cancellationToken = default, string? keyIvType = "text")
    {
        await using FileStream inputStream = new(inputFilePath, FileMode.Open, FileAccess.Read);
        long totalBytes = inputStream.Length;

        try
        {
            await using FileStream outputStream = new(outputFilePath, FileMode.Create, FileAccess.Write);
            using Aes aesAlg = Aes.Create();
            SetKeyAndIv(aesAlg, key, iv, keyIvType);

            using ICryptoTransform encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);
            await using CryptoStream cryptoStream = new(outputStream, encryptor, CryptoStreamMode.Write);

            byte[] buffer = new byte[BufferSize];
            int bytesRead;
            long processedBytes = 0;
            while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await cryptoStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                processedBytes += bytesRead;
                progress?.Report((double)processedBytes / totalBytes);
            }
            await cryptoStream.FlushFinalBlockAsync(cancellationToken);
            progress?.Report(1.0);

            _logger.LogInformation("File encrypted: {Input} -> {Output}", inputFilePath, outputFilePath);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(outputFilePath)) File.Delete(outputFilePath); } catch { }
            _logger.LogError(ex, "File encryption failed: {Input}", inputFilePath);
            throw;
        }
    }

    public async Task DecryptFileAsync(string inputFilePath, string outputFilePath, string key, string iv, IProgress<double>? progress = null, CancellationToken cancellationToken = default, string? keyIvType = "text")
    {
        await using FileStream inputStream = new(inputFilePath, FileMode.Open, FileAccess.Read);
        long totalBytes = inputStream.Length;

        try
        {
            await using FileStream outputStream = new(outputFilePath, FileMode.Create, FileAccess.Write);
            using Aes aesAlg = Aes.Create();
            SetKeyAndIv(aesAlg, key, iv, keyIvType);

            using ICryptoTransform decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);
            await using CryptoStream cryptoStream = new(inputStream, decryptor, CryptoStreamMode.Read);

            byte[] buffer = new byte[BufferSize];
            int bytesRead;
            long processedBytes = 0;
            while ((bytesRead = await cryptoStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await outputStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                processedBytes += bytesRead;
                progress?.Report((double)processedBytes / totalBytes);
            }
            progress?.Report(1.0);

            _logger.LogInformation("File decrypted: {Input} -> {Output}", inputFilePath, outputFilePath);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(outputFilePath)) File.Delete(outputFilePath); } catch { }
            _logger.LogError(ex, "File decryption failed: {Input}", inputFilePath);
            throw;
        }
    }

    private static void SetKeyAndIv(Aes aesAlg, string key, string iv, string? keyIvType)
    {
        switch (keyIvType)
        {
            case "hex":
                aesAlg.Key = Hex.Decode(key);
                if (!string.IsNullOrEmpty(iv)) aesAlg.IV = Hex.Decode(iv);
                break;
            case "text":
                aesAlg.Key = Encoding.UTF8.GetBytes(key);
                if (!string.IsNullOrEmpty(iv)) aesAlg.IV = Encoding.UTF8.GetBytes(iv);
                break;
            case "base64":
                aesAlg.Key = Convert.FromBase64String(key);
                if (!string.IsNullOrEmpty(iv)) aesAlg.IV = Convert.FromBase64String(iv);
                break;
            default:
                throw new ArgumentException($"Unsupported keyIvType: {keyIvType}");
        }
        if (aesAlg.Key.Length is not (16 or 24 or 32))
            throw new ArgumentException($"Key must be 16, 24, or 32 bytes, got {aesAlg.Key.Length}");
        if (aesAlg.IV.Length != 16)
            throw new ArgumentException($"IV must be 16 bytes, got {aesAlg.IV.Length}");
    }
}