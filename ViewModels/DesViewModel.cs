using Lang.Avalonia;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LittleFancyToolAva.ViewModels
{
    public class DesViewModel : SymmetricCipherViewModelBase, IViewState
    {
        public override int[] KeyLengthOptions => [64];

        string IViewState.ViewName => "desView";

        public DesViewModel([FromKeyedServices("DES")] IEncryptionSymmetric encryption, INotificationService notificationService, IViewStateService viewStateService)
            : base(encryption, notificationService)
        {
            DisplayTitle = LocalizationRegistry.Get("Encrypt.DES_Title");
            DisplaySubtitle = LocalizationRegistry.Get("Encrypt.DES_Subtitle");
            Paddings = ["PKCS7", "Zeros", "None"];
            GenerateSymmetricKey();
            viewStateService.Register(this);
        }

        protected override (string TitleKey, string SubtitleKey) GetTitleKeys() =>
            ("Encrypt.DES_Title", "Encrypt.DES_Subtitle");

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
