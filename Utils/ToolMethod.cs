using System.Security.Cryptography;
using System.Text;

namespace LittleFancyToolAva.Utils
{
    public static class ToolMethod
    {
        private static readonly Random _random = new();

        public static bool GetRandomBoolean(int probabilityPercent)
        {
            return _random.Next(100) < probabilityPercent;
        }

        public static string ByteArrayToHexString(byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", " ");
        }

        public static byte[] HexStringToBytes(string hexStr)
        {
            try
            {
                string hex = hexStr.Replace(" ", "");
                int length = hex.Length;
                byte[] bytes = new byte[length / 2];
                for (int i = 0; i < length; i += 2)
                {
                    bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
                }
                return bytes;
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
            }
        }

        public static string GenerateSymmetricKey(int bitLength, string keyIvType)
        {
            const string ValidChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()_+-=[]{}|;':\",./<>?";
            byte[] keyBytes = new byte[bitLength / 8];
            int length = bitLength switch
            {
                64 => 8,
                128 => 16,
                192 => 24,
                256 => 32,
                _ => throw new ArgumentException("Invalid bit length. Supported: 64, 128, 192, 256.")
            };
            if (keyIvType == "text")
            {
                var key = new StringBuilder(bitLength);
                for (int i = 0; i < length; i++)
                {
                    int index = _random.Next(0, ValidChars.Length);
                    key.Append(ValidChars[index]);
                }
                return key.ToString();
            }
            if (keyIvType == "base64")
            {
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(keyBytes);
                }
                return Convert.ToBase64String(keyBytes);
            }
            if (keyIvType == "hex")
            {
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(keyBytes);
                }
                return HexToStr(keyBytes);
            }
            return "keyIvType error";
        }

        private static string HexToStr(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        public static CipherMode EncryptMode(string mode)
        {
            switch (mode)
            {
                case "ECB":
                    return CipherMode.ECB;
                case "CBC":
                    return CipherMode.CBC;
                default:
                    throw new NotSupportedException("不支持的加密模式");
            }
        }

        public static Encoding GetEncoding(EncodingMode encodingMode)
        {
            return encodingMode switch
            {
                EncodingMode.Auto => Encoding.UTF8,
                EncodingMode.ASCII => Encoding.ASCII,
                EncodingMode.UTF8 => Encoding.UTF8,
                EncodingMode.GB2312 => Encoding.GetEncoding("GB18030"),
                _ => throw new ArgumentException("不支持的编码类型"),
            };
        }

        public static byte[] GetEncodedData(string input, EncodingMode mode)
        {
            return mode switch
            {
                EncodingMode.Auto => Encoding.UTF8.GetBytes(input),
                EncodingMode.ASCII => Encoding.ASCII.GetBytes(input),
                EncodingMode.UTF8 => Encoding.UTF8.GetBytes(input),
                EncodingMode.GB2312 => Encoding.GetEncoding("GB18030").GetBytes(input),
                _ => throw new ArgumentException("不支持的编码类型"),
            };
        }

        public static string CalculateFileHash(string filePath, string mode)
        {
            using FileStream stream = File.OpenRead(filePath);
            HashAlgorithm hashAlgorithm = mode.ToUpperInvariant() switch
            {
                "MD5" => MD5.Create(),
                "SHA1" => SHA1.Create(),
                "SHA256" => SHA256.Create(),
                "SHA384" => SHA384.Create(),
                "SHA512" => SHA512.Create(),
                _ => throw new NotSupportedException($"Hash mode '{mode}' is not supported.")
            };
            using (hashAlgorithm)
            {
                byte[] hashBytes = hashAlgorithm.ComputeHash(stream);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }

        public static bool IsValidFolderPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }

        public static bool IsMusicFile(string path)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
            return ext is ".mp3" or ".wav" or ".flac" or ".ogg";
        }

        public enum EncodingMode
        {
            Auto,
            UTF8,
            ASCII,
            GB2312
        }
    }
}
