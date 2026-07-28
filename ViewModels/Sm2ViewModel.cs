using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lang.Avalonia;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels
{
    public partial class Sm2ViewModel : AsymmetricCipherViewModelBase, IViewState
    {
        public int ModeIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        private readonly string[] _modes = ["C1C2C3", "C1C3C2"];

        string IViewState.ViewName => "sm2View";

        public Sm2ViewModel(IViewStateService viewStateService) : base(new SM2Encryption())
        {
            DisplayTitle = LocalizationRegistry.Get("Encrypt.SM2_Title");
            DisplaySubtitle = LocalizationRegistry.Get("Encrypt.SM2_Subtitle");
            GenerateKeyPair();
            viewStateService.Register(this);

            I18nManager.Instance.CultureChanged += (_, _) =>
            {
                DisplayTitle = LocalizationRegistry.Get("Encrypt.SM2_Title");
                DisplaySubtitle = LocalizationRegistry.Get("Encrypt.SM2_Subtitle");
            };
        }

        object IViewState.CaptureState() => new Sm2ViewState
        {
            InputText = InputText,
            ModeIndex = ModeIndex
        };

        void IViewState.RestoreState(object state)
        {
            if (state is Sm2ViewState s)
            {
                InputText = s.InputText;
                ModeIndex = s.ModeIndex;
            }
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
