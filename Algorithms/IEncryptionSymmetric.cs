namespace LittleFancyToolAva.Algorithms
{
    public interface IEncryptionSymmetric
    {
        int KeyBitLength { get; }
        int IvBitLength { get; }

        string Encrypt(
            string input,
            string? key = null,
            string? paddingMode = null,
            int keyLength = 128,
            string? iv = null,
            string? mode = null,
            string? outputType = "base64",
            string? keyIvType = "text");
        string Decrypt(
            string input,
            string? key = null,
            string? paddingMode = null,
            int keyLength = 128,
            string? iv = null,
            string? mode = null,
            string? outputType = "base64",
            string? keyIvType = "text");
    }
}


