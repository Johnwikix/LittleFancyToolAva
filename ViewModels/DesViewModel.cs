using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LittleFancyToolAva.ViewModels
{
    public class DesViewModel : SymmetricCipherViewModelBase
    {
        public override int KeyBitLength => 64;

        public DesViewModel([FromKeyedServices("DES")] IEncryptionSymmetric encryption, INotificationService notificationService)
            : base(encryption, notificationService)
        {
            DisplayTitle = "DES 加解密";
            DisplaySubtitle = "数据加密标准 (DES) 对称加解密";
            Paddings = ["PKCS7", "Zeros", "None"];
            GenerateSymmetricKey();
        }
    }
}
