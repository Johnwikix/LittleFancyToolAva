using Lang.Avalonia;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LittleFancyToolAva.ViewModels
{
    public class Sm4ViewModel : SymmetricCipherViewModelBase, IViewState
    {
        public override int[] KeyLengthOptions => [128];

        string IViewState.ViewName => "sm4View";

        public Sm4ViewModel([FromKeyedServices("SM4")] IEncryptionSymmetric encryption, INotificationService notificationService, IViewStateService viewStateService)
            : base(encryption, notificationService)
        {
            DisplayTitle = LocalizationRegistry.Get("Encrypt.SM4_Title");
            DisplaySubtitle = LocalizationRegistry.Get("Encrypt.SM4_Subtitle");
            Paddings = ["PKCS7", "ISO10126", "ZEROBYTE"];
            GenerateSymmetricKey();
            viewStateService.Register(this);
        }

        protected override (string TitleKey, string SubtitleKey) GetTitleKeys() =>
            ("Encrypt.SM4_Title", "Encrypt.SM4_Subtitle");

        object IViewState.CaptureState() => new SymmetricCipherViewState
        {
            InputText = InputText,
            PaddingIndex = PaddingIndex,
            EncryptModeIndex = EncryptModeIndex,
            OutputTypeIndex = OutputTypeIndex,
            KeyIvTypeIndex = KeyIvTypeIndex
        };

        void IViewState.RestoreState(object state)
        {
            if (state is SymmetricCipherViewState s)
            {
                InputText = s.InputText;
                PaddingIndex = s.PaddingIndex;
                EncryptModeIndex = s.EncryptModeIndex;
                OutputTypeIndex = s.OutputTypeIndex;
                KeyIvTypeIndex = s.KeyIvTypeIndex;
            }
        }
    }
}
