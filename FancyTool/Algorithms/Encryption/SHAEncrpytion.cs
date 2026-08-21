using System.Security.Cryptography;
using System.Text;

namespace FancyToolAva.Algorithms.Encryption
{
    public class SHAEncrpytion : IEncryptionAbstract
    {
        public string Decrypt(string input)
        {
            throw new NotImplementedException();
        }

        public string Encrypt(string input, string? upperLowerCase, int outputLength, string? mode)
        {
            using HashAlgorithm algorithm = GetHashAlgorithm(mode);
            byte[] inputBytes = Encoding.UTF8.GetBytes(input);
            byte[] hashBytes = algorithm.ComputeHash(inputBytes);
            string format = upperLowerCase == "UPPER" ? "X2" : "x2";
            StringBuilder sb = new StringBuilder();
            foreach (byte b in hashBytes)
            {
                sb.Append(b.ToString(format));
            }
            return sb.ToString();
        }

        private static HashAlgorithm GetHashAlgorithm(string algorithmName)
        {
            return algorithmName.ToUpper() switch
            {
                "SHA1" => SHA1.Create(),
                "SHA256" => SHA256.Create(),
                "SHA384" => SHA384.Create(),
                "SHA512" => SHA512.Create(),
                _ => SHA256.Create(),
            };
        }

    }
}


