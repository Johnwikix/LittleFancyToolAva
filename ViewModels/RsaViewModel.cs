using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;

namespace LittleFancyToolAva.ViewModels
{
    public partial class RsaViewModel : AsymmetricCipherViewModelBase
    {
        [ObservableProperty]
        private int _paddingIndex;

        [ObservableProperty]
        private int _keyLengthIndex;

        [ObservableProperty]
        private int _keyFormatIndex;

        private readonly int[] _keyLengths = [1024, 2048, 4096];
        private readonly string[] _paddings = ["Pkcs1", "OaepSHA1", "OaepSHA256", "OaepSHA384", "OaepSHA512"];
        private readonly string[] _keyFormats = ["PKCS#1", "PKCS#8"];

        public RsaViewModel() : base(new RSAEncryption())
        {
            DisplayTitle = "RSA 加解密";
            DisplaySubtitle = "RSA 非对称加密算法，支持多种填充模式";
            GenerateKeyPair();
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            OutputText = _encryption.Encrypt(InputText, PublicKey, _paddings[PaddingIndex], _keyLengths[KeyLengthIndex]);
        }

        [RelayCommand]
        private void Decrypt()
        {
            if (string.IsNullOrEmpty(OutputText)) return;
            InputText = _encryption.Decrypt(OutputText, PrivateKey, _paddings[PaddingIndex], _keyLengths[KeyLengthIndex]);
        }

        [RelayCommand]
        private void GenerateKeyPair()
        {
            var (pub, priv) = _encryption.GenerateKeyPair(_keyLengths[KeyLengthIndex], _keyFormats[KeyFormatIndex]);
            PublicKey = pub;
            PrivateKey = priv;
        }
    }
}
