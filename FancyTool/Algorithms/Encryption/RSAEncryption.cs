using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;

namespace FancyToolAva.Algorithms.Encryption
{
    public class RSAEncryption : IEncryptionAsymmetric
    {
        public string Encrypt(string input, string publicKey = null, string paddingMode = "Pkcs1", int keyLength = 2048)
        {
            using (RSA rsa = RSA.Create())
            {
                rsa.ImportFromPem(publicKey);
                RSAEncryptionPadding padding = GetRSAEncryptionPadding(paddingMode);
                byte[] dataToEncrypt = Encoding.UTF8.GetBytes(input);
                byte[] encryptedData = rsa.Encrypt(dataToEncrypt, padding);
                return Convert.ToBase64String(encryptedData);
            }
        }

        public string Decrypt(string input, string privateKey = null, string paddingMode = "Pkcs1", int keyLength = 2048)
        {
            using (RSA rsa = RSA.Create())
            {
                rsa.ImportFromPem(privateKey);
                RSAEncryptionPadding padding = GetRSAEncryptionPadding(paddingMode);
                byte[] encryptedData = Convert.FromBase64String(input);
                byte[] decryptedData = rsa.Decrypt(encryptedData, padding);
                return Encoding.UTF8.GetString(decryptedData);
            }
        }

        public (string publicKey, string privateKey) GenerateKeyPair(int keyLength = 2048, string keyFormat = "PKCS#8")
        {
            using (RSA rsa = RSA.Create(keyLength))
            {
                string privateKeyPem = ExportPrivateKey(rsa, keyFormat);
                string publicKeyPem = ExportPublicKey(rsa, keyFormat);
                return (publicKeyPem, privateKeyPem);
            }
        }

        private static string ExportPrivateKey(RSA rsa, string keyFormat)
        {
            if (keyFormat == "PKCS#8")
            {
                return rsa.ExportPkcs8PrivateKeyPem();
            }
            else if (keyFormat == "PKCS#1")
            {
                return rsa.ExportRSAPrivateKeyPem();
            }
            else
            {
                throw new ArgumentException("Unsupported private key format", nameof(keyFormat));
            }
        }

        private static string ExportPublicKey(RSA rsa, string keyFormat)
        {
            if (keyFormat == "PKCS#1")
            {
                return rsa.ExportRSAPublicKeyPem();
            }
            else if (keyFormat == "PKCS#8")
            {
                return rsa.ExportSubjectPublicKeyInfoPem();
            }
            else
            {
                throw new ArgumentException("Unsupported public key format", nameof(keyFormat));
            }
        }

        private RSAEncryptionPadding GetRSAEncryptionPadding(string paddingMode)
        {
            switch (paddingMode)
            {
                case "Pkcs1":
                    return RSAEncryptionPadding.Pkcs1;
                case "OaepSHA1":
                    return RSAEncryptionPadding.OaepSHA1;
                case "OaepSHA256":
                    return RSAEncryptionPadding.OaepSHA256;
                case "OaepSHA384":
                    return RSAEncryptionPadding.OaepSHA384;
                case "OaepSHA512":
                    return RSAEncryptionPadding.OaepSHA512;
                default:
                    throw new NotSupportedException($"Unsupported RSA padding: {paddingMode}");
            }
        }

        public async Task EncryptFileAsync(string inputFilePath, string outputFilePath, string publicKey, int keyLength)
        {
            using (var rsa = RSA.Create())
            {
                rsa.ImportFromPem(publicKey);
                int keySizeBytes = rsa.KeySize / 8;
                int maxDataSize = keySizeBytes - 11;
                int encryptedBlockSize = keySizeBytes;

                await using (var inputFileStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read))
                await using (var tmpStream = new FileStream(outputFilePath + ".tmp", FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[maxDataSize];
                    int bytesRead;
                    while ((bytesRead = await inputFileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        byte[] dataToEncrypt = bytesRead == buffer.Length ? buffer : buffer[..bytesRead];
                        byte[] encryptedData = rsa.Encrypt(dataToEncrypt, RSAEncryptionPadding.Pkcs1);
                        await tmpStream.WriteAsync(encryptedData, 0, encryptedData.Length);
                    }
                }
                if (File.Exists(outputFilePath))
                    File.Delete(outputFilePath);
                File.Move(outputFilePath + ".tmp", outputFilePath);
            }
        }

        public async Task DecryptFileAsync(string inputFilePath, string outputFilePath, string privateKey, int keyLength)
        {
            using (var rsa = RSA.Create())
            {
                rsa.ImportFromPem(privateKey);
                int keySizeBytes = rsa.KeySize / 8;

                await using (var inputFileStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read))
                await using (var tmpStream = new FileStream(outputFilePath + ".tmp", FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[keySizeBytes];
                    int bytesRead;
                    while ((bytesRead = await inputFileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        byte[] blockToDecrypt = bytesRead == keySizeBytes ? buffer : buffer[..bytesRead];
                        byte[] decryptedData = rsa.Decrypt(blockToDecrypt, RSAEncryptionPadding.Pkcs1);
                        await tmpStream.WriteAsync(decryptedData, 0, decryptedData.Length);
                    }
                }
                if (File.Exists(outputFilePath))
                    File.Delete(outputFilePath);
                File.Move(outputFilePath + ".tmp", outputFilePath);
            }
        }
    }
}
