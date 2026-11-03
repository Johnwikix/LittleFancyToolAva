using FancyToolAva.Utils;
using Org.BouncyCastle.Utilities.Encoders;
using System.Security.Cryptography;
using System.Text;

namespace FancyToolAva.Algorithms
{
    public class AESEncryption : IEncryptionSymmetric
    {
        public int KeyBitLength => 128;
        public int IvBitLength => 128;

        public string Encrypt(string input, string? key = null, string paddingMode = "PKCS7", int keyLength = 128, string? iv = null, string mode = null, string? outputType = "base64", string? keyIvType = "text")
        {
            using (Aes aesAlg = Aes.Create())
            {
                SetKeyAndIv(aesAlg, key, iv, keyIvType, mode);
                aesAlg.Padding = GetAesPaddingMode(paddingMode);
                aesAlg.Mode = ToolMethod.EncryptMode(mode);
                ICryptoTransform encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);
                using (MemoryStream msEncrypt = new MemoryStream())
                {
                    using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                    {
                        using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
                        {
                            swEncrypt.Write(input);
                        }
                        byte[] encrypted = msEncrypt.ToArray();
                        return outputType switch
                        {
                            "base64" => Convert.ToBase64String(encrypted),
                            "hex" => Hex.ToHexString(encrypted),
                            _ => throw new NotSupportedException($"Unsupported output type: {outputType}")
                        };
                    }
                }
            }
        }

        public string Decrypt(string input, string? key = null, string paddingMode = "PKCS7", int keyLength = 128, string? iv = null, string mode = null, string? outputType = "base64", string? keyIvType = "text")
        {
            using (Aes aesAlg = Aes.Create())
            {
                SetKeyAndIv(aesAlg, key, iv, keyIvType, mode);
                aesAlg.Padding = GetAesPaddingMode(paddingMode);
                aesAlg.Mode = ToolMethod.EncryptMode(mode);
                ICryptoTransform decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);

                byte[] cipherBytes = outputType switch
                {
                    "base64" => Convert.FromBase64String(input),
                    "hex" => Hex.Decode(input),
                    _ => throw new NotSupportedException($"Unsupported output type: {outputType}")
                };

                using (MemoryStream msDecrypt = new MemoryStream(cipherBytes))
                {
                    using (CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                    {
                        using (StreamReader srDecrypt = new StreamReader(csDecrypt))
                        {
                            return srDecrypt.ReadToEnd();
                        }
                    }
                }
            }
        }

        private static void SetKeyAndIv(Aes aesAlg, string? key, string? iv, string? keyIvType, string mode)
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
            if (mode == "CBC" && aesAlg.IV.Length != 16)
                throw new ArgumentException($"IV must be 16 bytes for CBC mode, got {aesAlg.IV.Length}");
        }

        private PaddingMode GetAesPaddingMode(string paddingMode)
        {
            if (string.IsNullOrEmpty(paddingMode))
            {
                return PaddingMode.PKCS7;
            }

            switch (paddingMode)
            {
                case "PKCS7":
                    return PaddingMode.PKCS7;
                case "Zeros":
                    return PaddingMode.Zeros;
                case "None":
                    return PaddingMode.None;
                default:
                    throw new NotSupportedException($"Unsupported AES padding: {paddingMode}");
            }
        }
    }
}
