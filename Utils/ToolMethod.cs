using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace LittleFancyToolAva.Utils
{
    public static class ToolMethod
    {
        private const int FileHashBufferSize = 64 * 1024;

        public static bool GetRandomBoolean(int probabilityPercent)
        {
            return Random.Shared.Next(100) < probabilityPercent;
        }

        public static string ByteArrayToHexString(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return ByteArrayToHexString((ReadOnlySpan<byte>)data);
        }

        public static string ByteArrayToHexString(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return string.Empty;
            return Convert.ToHexString(data);
        }

        public static string ByteArrayToSpacedHexString(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return ByteArrayToSpacedHexString((ReadOnlySpan<byte>)data);
        }

        public static string ByteArrayToSpacedHexString(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return string.Empty;
            return string.Create(data.Length * 3 - 1, data, (span, src) =>
            {
                int pos = 0;
                for (int i = 0; i < src.Length; i++)
                {
                    if (i > 0)
                    {
                        span[pos++] = ' ';
                    }
                    byte b = src[i];
                    byte hi = (byte)(b >> 4);
                    byte lo = (byte)(b & 0x0F);
                    span[pos++] = (char)(hi < 10 ? '0' + hi : 'A' + hi - 10);
                    span[pos++] = (char)(lo < 10 ? '0' + lo : 'A' + lo - 10);
                }
            });
        }

        public static byte[] HexStringToBytes(string hexStr)
        {
            if (hexStr == null)
                throw new ArgumentNullException(nameof(hexStr));
            if (hexStr.Length == 0) return [];
            return HexStringToBytes(hexStr.AsSpan());
        }

        public static byte[] HexStringToBytes(ReadOnlySpan<char> hex)
        {
            int strippedLen = CountHexChars(hex);
            if (strippedLen % 2 != 0)
            {
                char[] padded = new char[strippedLen + 1];
                padded[0] = '0';
                int write = 1;
                for (int i = 0; i < hex.Length; i++)
                {
                    char c = hex[i];
                    if (c is not ' ' and not '-')
                    {
                        padded[write++] = c;
                    }
                }
                return Convert.FromHexString(padded);
            }
            if (strippedLen == 0) return [];
            return Convert.FromHexString(BuildHexString(hex, strippedLen));
        }

        public static bool TryHexStringToBytes(string? input, out byte[] bytes)
        {
            bytes = [];
            if (string.IsNullOrEmpty(input)) return true;
            try
            {
                foreach (char c in input)
                {
                    if (c is ' ' or '-') continue;
                    if (!IsHexChar(c))
                        return false;
                }
                bytes = HexStringToBytes(input.AsSpan());
                return true;
            }
            catch (FormatException)
            {
                bytes = [];
                return false;
            }
        }

        private static bool IsHexChar(char c) =>
            (c >= '0' && c <= '9') ||
            (c >= 'a' && c <= 'f') ||
            (c >= 'A' && c <= 'F');

        private static int CountHexChars(ReadOnlySpan<char> source)
        {
            int count = 0;
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                if (c is not ' ' and not '-') count++;
            }
            return count;
        }

        private static string BuildHexString(ReadOnlySpan<char> source, int length)
        {
            return string.Create(length, source, (span, src) =>
            {
                int write = 0;
                for (int i = 0; i < src.Length; i++)
                {
                    char c = src[i];
                    if (c is not ' ' and not '-')
                    {
                        span[write++] = c;
                    }
                }
            });
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
            return CalculateFileHash(stream, mode);
        }

        public static string CalculateFileHash(Stream stream, string mode)
        {
            ArgumentNullException.ThrowIfNull(stream);
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
                byte[] rented = ArrayPool<byte>.Shared.Rent(FileHashBufferSize);
                try
                {
                    int read;
                    Span<byte> buffer = rented.AsSpan(0, FileHashBufferSize);
                    while ((read = stream.Read(buffer)) > 0)
                    {
                        hashAlgorithm.TransformBlock(rented, 0, read, null, 0);
                    }
                    hashAlgorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return Convert.ToHexString(hashAlgorithm.Hash!).ToLowerInvariant();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented, clearArray: false);
                }
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