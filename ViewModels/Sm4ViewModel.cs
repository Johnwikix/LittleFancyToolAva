using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LittleFancyToolAva.ViewModels
{
    public class Sm4ViewModel : SymmetricCipherViewModelBase
    {
        public override int KeyBitLength => 128;

        public Sm4ViewModel([FromKeyedServices("SM4")] IEncryptionSymmetric encryption, INotificationService notificationService)
            : base(encryption, notificationService)
        {
            DisplayTitle = "SM4 加解密";
            DisplaySubtitle = "国密 SM4 对称分组密码算法";
            Paddings = ["PKCS7", "ISO10126", "ZEROBYTE"];
            GenerateSymmetricKey();
        }
    }
}
