using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;

namespace LittleFancyToolAva.ViewModels
{
    public partial class Sm2ViewModel : ViewModelBase
    {
        private readonly IEncryptionAsymmetric _encryption;
        [ObservableProperty] private string _inputText = string.Empty;
        [ObservableProperty] private string _outputText = string.Empty;
        [ObservableProperty] private string _publicKey = string.Empty;
        [ObservableProperty] private string _privateKey = string.Empty;
        [ObservableProperty] private int _modeIndex;

        public Sm2ViewModel()
        {
            _encryption = new SM2Encryption();
            GenerateKeyPair();
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            string[] modes = ["C1C2C3", "C1C3C2"];
            OutputText = _encryption.Encrypt(InputText, PublicKey, modes[ModeIndex]);
        }

        [RelayCommand]
        private void Decrypt()
        {
            if (string.IsNullOrEmpty(OutputText)) return;
            string[] modes = ["C1C2C3", "C1C3C2"];
            InputText = _encryption.Decrypt(OutputText, PrivateKey, modes[ModeIndex]);
        }

        [RelayCommand]
        private void GenerateKeyPair()
        {
            var (pub, priv) = _encryption.GenerateKeyPair();
            PublicKey = pub;
            PrivateKey = priv;
        }
    }
}
