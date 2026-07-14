using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;

namespace LittleFancyToolAva.ViewModels
{
    public partial class Sm2ViewModel : AsymmetricCipherViewModelBase
    {
        [ObservableProperty]
        private int _modeIndex;

        private readonly string[] _modes = ["C1C2C3", "C1C3C2"];

        public Sm2ViewModel() : base(new SM2Encryption())
        {
            DisplayTitle = "SM2 加解密";
            DisplaySubtitle = "国密 SM2 非对称椭圆曲线密码算法";
            GenerateKeyPair();
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            OutputText = _encryption.Encrypt(InputText, PublicKey, _modes[ModeIndex]);
        }

        [RelayCommand]
        private void Decrypt()
        {
            if (string.IsNullOrEmpty(OutputText)) return;
            InputText = _encryption.Decrypt(OutputText, PrivateKey, _modes[ModeIndex]);
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
