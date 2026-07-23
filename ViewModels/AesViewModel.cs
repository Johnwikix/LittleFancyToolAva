using CommunityToolkit.Mvvm.ComponentModel;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LittleFancyToolAva.ViewModels
{
    public partial class AesViewModel : SymmetricCipherViewModelBase, IViewState
    {
        public override int KeyLengthIndex
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnKeyLengthIndexChanged(value);
                }
            }
        }

        public override int[] KeyLengthOptions => [128, 192, 256];

        public override bool IsKeyLengthSelectable => true;

        string IViewState.ViewName => "aesView";

        public AesViewModel([FromKeyedServices("AES")] IEncryptionSymmetric encryption, INotificationService notificationService, IViewStateService viewStateService)
            : base(encryption, notificationService)
        {
            DisplayTitle = "AES 加解密";
            DisplaySubtitle = "高级加密标准 (AES, Rijndael) 对称加解密";
            Paddings = ["PKCS7", "Zeros", "None"];
            GenerateSymmetricKey();
            viewStateService.Register(this);
        }

        object IViewState.CaptureState() => new AesViewState
        {
            InputText = InputText,
            PaddingIndex = PaddingIndex,
            EncryptModeIndex = EncryptModeIndex,
            OutputTypeIndex = OutputTypeIndex,
            KeyIvTypeIndex = KeyIvTypeIndex,
            KeyLengthIndex = KeyLengthIndex
        };

        void IViewState.RestoreState(object state)
        {
            if (state is AesViewState s)
            {
                InputText = s.InputText;
                PaddingIndex = s.PaddingIndex;
                EncryptModeIndex = s.EncryptModeIndex;
                OutputTypeIndex = s.OutputTypeIndex;
                KeyIvTypeIndex = s.KeyIvTypeIndex;
                KeyLengthIndex = s.KeyLengthIndex;
            }
        }

        private void OnKeyLengthIndexChanged(int value)
        {
            GenerateSymmetricKey();
        }
    }
}
