using CommunityToolkit.Mvvm.ComponentModel;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LittleFancyToolAva.ViewModels
{
    public partial class AesViewModel : SymmetricCipherViewModelBase, IViewState
    {
        [ObservableProperty]
        private int _keyLengthIndex;

        public override int KeyBitLength => KeyLengthIndex switch
        {
            1 => 192,
            2 => 256,
            _ => 128
        };

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

        partial void OnKeyLengthIndexChanged(int value)
        {
            GenerateSymmetricKey();
        }
    }
}
