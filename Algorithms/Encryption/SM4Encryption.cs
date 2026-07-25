using LittleFancyToolAva.Algorithms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Utilities.Encoders;
using System.Text;

namespace LittleFancyToolAva.Algorithms.Encryption
{
    public class SM4Encryption : IEncryptionSymmetric
    {
        public int KeyBitLength => 128;
        public int IvBitLength => 128;

        public string Decrypt(
            string input,
            string? key, string?
            paddingModeStr,
            int keyLength,
            string? iv,
            string mode,
            string? outputType = "base64",
            string? keyIvType = "text")
        {
            byte[] cipherBytes = outputType switch
            {
                "base64" => Convert.FromBase64String(input),
                "hex" => Hex.Decode(input),
                _ => throw new NotSupportedException($"Unsupported output type: {outputType}")
            };

            var (keyBytes, ivBytes) = ParseKeyIv(key, iv, keyIvType);
            IBlockCipherPadding padding = GetPadding(paddingModeStr);
            IBufferedCipher cipher = CreateCipher(mode, ivBytes, keyBytes, padding, false);

            byte[] outputBytes = new byte[cipher.GetOutputSize(cipherBytes.Length)];
            int length = cipher.ProcessBytes(cipherBytes, 0, cipherBytes.Length, outputBytes, 0);
            cipher.DoFinal(outputBytes, length);
            return Encoding.UTF8.GetString(outputBytes);
        }

        public string Encrypt(
            string input,
            string? key,
            string? paddingModeStr,
            int keyLength,
            string? iv,
            string mode,
            string? outputType = "base64",
            string? keyIvType = "text")
        {
            var (keyBytes, ivBytes) = ParseKeyIv(key, iv, keyIvType);
            byte[] data = Encoding.UTF8.GetBytes(input);
            IBlockCipherPadding paddingMode = GetPadding(paddingModeStr);
            IBufferedCipher cipher = CreateCipher(mode, ivBytes, keyBytes, paddingMode, true);

            byte[] outputBytes = new byte[cipher.GetOutputSize(data.Length)];
            int length = cipher.ProcessBytes(data, 0, data.Length, outputBytes, 0);
            int finalLen = cipher.DoFinal(outputBytes, length);

            int actualLen = length + finalLen;
            return outputType switch
            {
                "base64" => Convert.ToBase64String(outputBytes, 0, actualLen),
                "hex" => Hex.ToHexString(outputBytes, 0, actualLen),
                _ => throw new NotSupportedException($"Unsupported output type: {outputType}")
            };
        }

        private static (byte[] key, byte[] iv) ParseKeyIv(string? key, string? iv, string? keyIvType)
        {
            byte[] keyBytes, ivBytes;
            switch (keyIvType)
            {
                case "hex":
                    keyBytes = Hex.Decode(key);
                    ivBytes = Hex.Decode(iv);
                    break;
                case "text":
                    keyBytes = Encoding.UTF8.GetBytes(key);
                    ivBytes = Encoding.UTF8.GetBytes(iv);
                    break;
                case "base64":
                    keyBytes = Convert.FromBase64String(key);
                    ivBytes = Convert.FromBase64String(iv);
                    break;
                default:
                    throw new ArgumentException($"Unsupported keyIvType: {keyIvType}");
            }
            if (keyBytes.Length != 16)
                throw new ArgumentException($"SM4 key must be 16 bytes, got {keyBytes.Length}");
            if (ivBytes.Length != 16)
                throw new ArgumentException($"SM4 IV must be 16 bytes, got {ivBytes.Length}");
            return (keyBytes, ivBytes);
        }

        private static IBufferedCipher CreateCipher(string mode, byte[]? ivBytes, byte[] keyBytes, IBlockCipherPadding padding, bool forEncryption)
        {
            IBlockCipher engine = new SM4Engine();
            string modeUpper = mode?.ToUpper() ?? "ECB";
            if (modeUpper == "ECB")
            {
                var cipher = new PaddedBufferedBlockCipher(engine, padding);
                cipher.Init(forEncryption, new KeyParameter(keyBytes));
                return cipher;
            }
            else if (modeUpper == "CBC")
            {
                if (ivBytes is null || ivBytes.Length == 0)
                {
                    throw new ArgumentException("CBC mode requires a non-null IV.");
                }
                var cipher = new PaddedBufferedBlockCipher(new CbcBlockCipher(engine), padding);
                cipher.Init(forEncryption, new ParametersWithIV(new KeyParameter(keyBytes), ivBytes));
                return cipher;
            }
            else
            {
                throw new ArgumentException($"Unsupported mode: {mode}. Supported: ECB, CBC.");
            }
        }

        private IBlockCipherPadding GetPadding(string paddingMode)
        {
            if (string.IsNullOrEmpty(paddingMode))
            {
                return new Pkcs7Padding();
            }

            switch (paddingMode.ToUpper())
            {
                case "PKCS7":
                    return new Pkcs7Padding();
                case "ISO10126":
                    return new ISO10126d2Padding();
                case "ZEROBYTE":
                    return new ZeroBytePadding();
                default:
                    throw new ArgumentException($"Unsupported padding: {paddingMode}. Supported: PKCS7, ISO10126, ZEROBYTE.");
            }
        }
    }
}
