using System.Threading.Tasks;
using System.Threading;
using System.Security.Cryptography;
using System.Text;

namespace LittleFancyToolAva.Algorithms.Encryption
{
    public class FileEncryptor : IFileEncryption
    {
        private const int BufferSize = 1024 * 1024;

        public async Task EncryptFileAsync(string inputFilePath, string outputFilePath, string key, string iv, CancellationToken cancellationToken = default)
        {
            string tmpPath = outputFilePath + ".tmp";
            try
            {
                await using FileStream inputStream = new(inputFilePath, FileMode.Open, FileAccess.Read);
                await using FileStream tmpStream = new(tmpPath, FileMode.Create, FileAccess.Write);
                using Aes aesAlg = Aes.Create();

                SetKeyAndIv(aesAlg, key, iv);
                using ICryptoTransform encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);
                await using CryptoStream cryptoStream = new(tmpStream, encryptor, CryptoStreamMode.Write);

                byte[] buffer = new byte[BufferSize];
                int bytesRead;
                while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await cryptoStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                }
                await cryptoStream.FlushFinalBlockAsync(cancellationToken);

                if (File.Exists(outputFilePath))
                    File.Delete(outputFilePath);
                File.Move(tmpPath, outputFilePath);
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
        }

        public async Task DecryptFileAsync(string inputFilePath, string outputFilePath, string key, string iv, CancellationToken cancellationToken = default)
        {
            string tmpPath = outputFilePath + ".tmp";
            try
            {
                await using FileStream inputStream = new(inputFilePath, FileMode.Open, FileAccess.Read);
                await using FileStream tmpStream = new(tmpPath, FileMode.Create, FileAccess.Write);
                using Aes aesAlg = Aes.Create();

                SetKeyAndIv(aesAlg, key, iv);
                using ICryptoTransform decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);
                await using CryptoStream cryptoStream = new(inputStream, decryptor, CryptoStreamMode.Read);

                byte[] buffer = new byte[BufferSize];
                int bytesRead;
                while ((bytesRead = await cryptoStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await tmpStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                }

                if (File.Exists(outputFilePath))
                    File.Delete(outputFilePath);
                File.Move(tmpPath, outputFilePath);
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
        }

        private static void SetKeyAndIv(Aes aesAlg, string key, string iv)
        {
            aesAlg.Key = Encoding.UTF8.GetBytes(key);
            aesAlg.IV = Encoding.UTF8.GetBytes(iv);
            if (aesAlg.Key.Length is not (16 or 24 or 32))
                throw new ArgumentException($"Key must be 16, 24, or 32 bytes, got {aesAlg.Key.Length}");
            if (aesAlg.IV.Length != 16)
                throw new ArgumentException($"IV must be 16 bytes, got {aesAlg.IV.Length}");
        }
    }
}
