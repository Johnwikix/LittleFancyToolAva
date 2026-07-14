using CommunityToolkit.Mvvm.ComponentModel;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LittleFancyToolAva.ViewModels
{
    public partial class AesViewModel : SymmetricCipherViewModelBase
    {
        [ObservableProperty]
        private int _keyLengthIndex;

        public override int KeyBitLength => KeyLengthIndex switch
        {
            1 => 192,
            2 => 256,
            _ => 128
        };

        public AesViewModel([FromKeyedServices("AES")] IEncryptionSymmetric encryption, INotificationService notificationService)
            : base(encryption, notificationService)
        {
            DisplayTitle = "AES 加解密";
            DisplaySubtitle = "高级加密标准 (AES, Rijndael) 对称加解密";
            Paddings = ["PKCS7", "Zeros", "None"];
            GenerateSymmetricKey();
        }

        partial void OnKeyLengthIndexChanged(int value)
        {
            GenerateSymmetricKey();
        }
    }
}
