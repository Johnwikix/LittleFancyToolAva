using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lang.Avalonia;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels
{
    public partial class RsaViewModel : AsymmetricCipherViewModelBase, IViewState
    {
        public int PaddingIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int KeyLengthIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int KeyFormatIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        private readonly int[] _keyLengths = [1024, 2048, 4096];
        private readonly string[] _paddings = ["Pkcs1", "OaepSHA1", "OaepSHA256", "OaepSHA384", "OaepSHA512"];
        private readonly string[] _keyFormats = ["PKCS#1", "PKCS#8"];

        string IViewState.ViewName => "rsaView";

        public RsaViewModel(IViewStateService viewStateService) : base(new RSAEncryption())
        {
            DisplayTitle = LocalizationRegistry.Get("Encrypt.RSA_Title");
            DisplaySubtitle = LocalizationRegistry.Get("Encrypt.RSA_Subtitle");
            GenerateKeyPair();
            viewStateService.Register(this);

            I18nManager.Instance.CultureChanged += (_, _) =>
            {
                DisplayTitle = LocalizationRegistry.Get("Encrypt.RSA_Title");
                DisplaySubtitle = LocalizationRegistry.Get("Encrypt.RSA_Subtitle");
            };
        }

        object IViewState.CaptureState() => new RsaViewState
        {
            InputText = InputText,
            PaddingIndex = PaddingIndex,
            KeyLengthIndex = KeyLengthIndex,
            KeyFormatIndex = KeyFormatIndex
        };

        void IViewState.RestoreState(object state)
        {
            if (state is RsaViewState s)
            {
                InputText = s.InputText;
                PaddingIndex = s.PaddingIndex;
                KeyLengthIndex = s.KeyLengthIndex;
                KeyFormatIndex = s.KeyFormatIndex;
            }
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
