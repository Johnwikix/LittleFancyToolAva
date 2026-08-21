using FancyToolAva.Utils;
using Org.BouncyCastle.Utilities.Encoders;
using System.Security.Cryptography;
using System.Text;

namespace FancyToolAva.Algorithms
{
    public class DESEncryption : IEncryptionSymmetric
    {
        public int KeyBitLength => 64;
        public int IvBitLength => 64;

        public string Encrypt(string input, string key = null, string paddingMode = "PKCS7", int keyLength = 64, string iv = null, string mode = null, string? outputType = "base64", string? keyIvType = "text")
        {
            using (DES desAlg = DES.Create())
            {
                SetKeyAndIv(desAlg, key, iv, keyIvType, mode);
                desAlg.Padding = GetPaddingMode(paddingMode);
                desAlg.Mode = ToolMethod.EncryptMode(mode);

                ICryptoTransform encryptor = desAlg.CreateEncryptor(desAlg.Key, desAlg.IV);

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

        public string Decrypt(string input, string key = null, string paddingMode = "PKCS7", int keyLength = 64, string iv = null, string mode = null, string? outputType = "base64", string? keyIvType = "text")
        {
            using (DES desAlg = DES.Create())
            {
                SetKeyAndIv(desAlg, key, iv, keyIvType, mode);
                desAlg.Padding = GetPaddingMode(paddingMode);
                desAlg.Mode = ToolMethod.EncryptMode(mode);

                ICryptoTransform decryptor = desAlg.CreateDecryptor(desAlg.Key, desAlg.IV);

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

        private static void SetKeyAndIv(DES desAlg, string? key, string? iv, string? keyIvType, string mode)
        {
            switch (keyIvType)
            {
                case "hex":
                    desAlg.Key = Hex.Decode(key);
                    if (!string.IsNullOrEmpty(iv)) desAlg.IV = Hex.Decode(iv);
                    break;
                case "text":
                    desAlg.Key = Encoding.UTF8.GetBytes(key);
                    if (!string.IsNullOrEmpty(iv)) desAlg.IV = Encoding.UTF8.GetBytes(iv);
                    break;
                case "base64":
                    desAlg.Key = Convert.FromBase64String(key);
                    if (!string.IsNullOrEmpty(iv)) desAlg.IV = Convert.FromBase64String(iv);
                    break;
                default:
                    throw new ArgumentException($"Unsupported keyIvType: {keyIvType}");
            }
            if (desAlg.Key.Length != 8)
                throw new ArgumentException($"DES key must be 8 bytes, got {desAlg.Key.Length}");
            if (mode == "CBC" && desAlg.IV.Length != 8)
                throw new ArgumentException($"DES IV must be 8 bytes for CBC mode, got {desAlg.IV.Length}");
        }

        private PaddingMode GetPaddingMode(string paddingMode)
        {
            switch (paddingMode)
            {
                case "PKCS7":
                    return PaddingMode.PKCS7;
                case "None":
                    return PaddingMode.None;
                case "Zeros":
                    return PaddingMode.Zeros;
                default:
                    throw new NotSupportedException($"Unsupported padding mode: {paddingMode}");
            }
        }
    }
}
