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
            DisplayTitle = "SM4 加解密";
            DisplaySubtitle = "国密 SM4 对称分组密码算法";
            Paddings = ["PKCS7", "ISO10126", "ZEROBYTE"];
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
