using LittleFancyToolAva.Algorithms.Encryption;
using System.Security.Cryptography;
using System.Text;

namespace LittleFancyToolAva.Services;

public class FileEncryptionService : IFileEncryptionService
{
    private const int BufferSize = 1024 * 1024;

    public async Task EncryptFileAsync(string inputFilePath, string outputFilePath, string key, string iv, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using FileStream inputStream = new(inputFilePath, FileMode.Open, FileAccess.Read);
        long totalBytes = inputStream.Length;
        long processedBytes = 0;

        using FileStream outputStream = new(outputFilePath, FileMode.Create, FileAccess.Write);
        using Aes aesAlg = Aes.Create();
        aesAlg.Key = Encoding.UTF8.GetBytes(key);
        aesAlg.IV = Encoding.UTF8.GetBytes(iv);

        using ICryptoTransform encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);
        using CryptoStream cryptoStream = new(outputStream, encryptor, CryptoStreamMode.Write);

        byte[] buffer = new byte[BufferSize];
        int bytesRead;
        while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await cryptoStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            processedBytes += bytesRead;
            progress?.Report((double)processedBytes / totalBytes);
        }
        await cryptoStream.FlushFinalBlockAsync(cancellationToken);
        progress?.Report(1.0);
    }

    public async Task DecryptFileAsync(string inputFilePath, string outputFilePath, string key, string iv, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using FileStream inputStream = new(inputFilePath, FileMode.Open, FileAccess.Read);
        long totalBytes = inputStream.Length;
        long processedBytes = 0;

        using FileStream outputStream = new(outputFilePath, FileMode.Create, FileAccess.Write);
        using Aes aesAlg = Aes.Create();
        aesAlg.Key = Encoding.UTF8.GetBytes(key);
        aesAlg.IV = Encoding.UTF8.GetBytes(iv);

        using ICryptoTransform decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);
        using CryptoStream cryptoStream = new(inputStream, decryptor, CryptoStreamMode.Read);

        byte[] buffer = new byte[BufferSize];
        int bytesRead;
        while ((bytesRead = await cryptoStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await outputStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            processedBytes += bytesRead;
            progress?.Report((double)processedBytes / totalBytes);
        }
        progress?.Report(1.0);
    }
}
