using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;

namespace LittleFancyToolAva.ViewModels
{
    public partial class RsaViewModel : ViewModelBase
    {
        private readonly IEncryptionAsymmetric _encryption;
        [ObservableProperty] private string _inputText = string.Empty;
        [ObservableProperty] private string _outputText = string.Empty;
        [ObservableProperty] private string _publicKey = string.Empty;
        [ObservableProperty] private string _privateKey = string.Empty;
        [ObservableProperty] private int _paddingIndex;
        [ObservableProperty] private int _keyLengthIndex;
        [ObservableProperty] private int _keyFormatIndex;

        public RsaViewModel()
        {
            _encryption = new RSAEncryption();
            GenerateKeyPair();
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            string[] paddings = ["Pkcs1", "OaepSHA1", "OaepSHA256", "OaepSHA384", "OaepSHA512"];
            int[] keyLengths = [1024, 2048, 4096];
            OutputText = _encryption.Encrypt(InputText, PublicKey, paddings[PaddingIndex], keyLengths[KeyLengthIndex]);
        }

        [RelayCommand]
        private void Decrypt()
        {
            if (string.IsNullOrEmpty(OutputText)) return;
            string[] paddings = ["Pkcs1", "OaepSHA1", "OaepSHA256", "OaepSHA384", "OaepSHA512"];
            int[] keyLengths = [1024, 2048, 4096];
            InputText = _encryption.Decrypt(OutputText, PrivateKey, paddings[PaddingIndex], keyLengths[KeyLengthIndex]);
        }

        [RelayCommand]
        private void GenerateKeyPair()
        {
            int[] keyLengths = [1024, 2048, 4096];
            string[] keyFormats = ["PKCS#1", "PKCS#8"];
            var (pub, priv) = _encryption.GenerateKeyPair(keyLengths[KeyLengthIndex], keyFormats[KeyFormatIndex]);
            PublicKey = pub;
            PrivateKey = priv;
        }
    }
}
