using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LittleFancyToolAva.ViewModels
{
    public class DesViewModel : SymmetricCipherViewModelBase, IViewState
    {
        public override int KeyBitLength => 64;

        string IViewState.ViewName => "desView";

        public DesViewModel([FromKeyedServices("DES")] IEncryptionSymmetric encryption, INotificationService notificationService, IViewStateService viewStateService)
            : base(encryption, notificationService)
        {
            DisplayTitle = "DES 加解密";
            DisplaySubtitle = "数据加密标准 (DES) 对称加解密";
            Paddings = ["PKCS7", "Zeros", "None"];
            GenerateSymmetricKey();
            viewStateService.Register(this);
        }

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
