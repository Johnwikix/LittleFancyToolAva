using System.Security.Cryptography;
using System.Text;

namespace LittleFancyToolAva.Utils
{
    public static class ToolMethod
    {
        public static bool GetRandomBoolean(int probabilityPercent)
        {
            return Random.Shared.Next(100) < probabilityPercent;
        }

        public static string ByteArrayToHexString(byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", " ");
        }

        public static byte[] HexStringToBytes(string hexStr)
        {
            if (string.IsNullOrWhiteSpace(hexStr))
                throw new ArgumentException("Hex string cannot be empty", nameof(hexStr));
            string hex = hexStr.Replace(" ", "").Replace("-", "");
            if (hex.Length % 2 != 0)
                throw new FormatException($"Hex string has odd length ({hex.Length} chars) after removing delimiters");
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < hex.Length; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }
            return bytes;
        }

        public static string GenerateSymmetricKey(int bitLength, string keyIvType)
        {
            const string ValidChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()_+-=[]{}|;':\",./<>?";
            int length = bitLength switch
            {
                64 => 8,
                128 => 16,
                192 => 24,
                256 => 32,
                _ => throw new ArgumentException("Invalid bit length. Supported: 64, 128, 192, 256.")
            };
            byte[] keyBytes = new byte[length];
            if (keyIvType == "text")
            {
                var key = new StringBuilder(length);
                Span<byte> randBytes = stackalloc byte[length];
                RandomNumberGenerator.Fill(randBytes);
                for (int i = 0; i < length; i++)
                {
                    int index = randBytes[i] % ValidChars.Length;
                    key.Append(ValidChars[index]);
                }
                return key.ToString();
            }
            if (keyIvType == "base64")
            {
                RandomNumberGenerator.Fill(keyBytes);
                return Convert.ToBase64String(keyBytes);
            }
            if (keyIvType == "hex")
            {
                RandomNumberGenerator.Fill(keyBytes);
                return HexToStr(keyBytes);
            }
            throw new ArgumentException($"Unsupported keyIvType: {keyIvType}");
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
            return mode switch
            {
                "ECB" => CipherMode.ECB,
                "CBC" => CipherMode.CBC,
                _ => throw new NotSupportedException($"Unsupported cipher mode: {mode}")
            };
        }

        public static Encoding GetEncoding(EncodingMode encodingMode)
        {
            return encodingMode switch
            {
                EncodingMode.Auto => Encoding.UTF8,
                EncodingMode.ASCII => Encoding.ASCII,
                EncodingMode.UTF8 => Encoding.UTF8,
                EncodingMode.GB2312 => Encoding.GetEncoding("GB18030"),
                _ => throw new ArgumentException($"Unsupported encoding: {encodingMode}")
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
                _ => throw new ArgumentException($"Unsupported encoding: {mode}")
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
            return ext is ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" or ".opus" or ".wma" or ".aiff" or ".ape";
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
