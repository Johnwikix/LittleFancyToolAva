using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.Utils;
using Microsoft.Extensions.DependencyInjection;
using Org.BouncyCastle.Utilities.Encoders;
using System.Text;

namespace LittleFancyToolAva.ViewModels
{
    public partial class DesViewModel : ViewModelBase
    {
        private readonly IEncryptionSymmetric _encryption;
        private readonly INotificationService _notificationService;
        [ObservableProperty] private string _inputText = string.Empty;
        [ObservableProperty] private string _outputText = string.Empty;
        [ObservableProperty] private string _key = string.Empty;
        [ObservableProperty] private string _iv = string.Empty;
        [ObservableProperty] private int _paddingIndex;
        [ObservableProperty] private int _encryptModeIndex;
        [ObservableProperty] private int _outputTypeIndex;
        [ObservableProperty] private int _keyIvTypeIndex;

        public DesViewModel([FromKeyedServices("DES")] IEncryptionSymmetric encryption, INotificationService notificationService)
        {
            _encryption = encryption;
            _notificationService = notificationService;
            Key = ToolMethod.GenerateSymmetricKey(64, "text");
            Iv = ToolMethod.GenerateSymmetricKey(64, "text");
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            if (!ValidateSymmetricKeyLength(Key, 8, GetSelectedKeyIvType())) return;
            if (!ValidateSymmetricIvLength(Iv, 8, GetSelectedKeyIvType())) return;
            try
            {
                string[] paddings = ["PKCS7", "Zeros", "None"];
                string[] modes = ["ECB", "CBC"];
                string[] outputTypes = ["base64", "hex"];
                OutputText = _encryption.Encrypt(InputText, Key, paddings[PaddingIndex], 64, Iv, modes[EncryptModeIndex], outputTypes[OutputTypeIndex], GetSelectedKeyIvType());
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"加密失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private void Decrypt()
        {
            if (string.IsNullOrEmpty(OutputText)) return;
            if (!ValidateSymmetricKeyLength(Key, 8, GetSelectedKeyIvType())) return;
            if (!ValidateSymmetricIvLength(Iv, 8, GetSelectedKeyIvType())) return;
            try
            {
                string[] paddings = ["PKCS7", "Zeros", "None"];
                string[] modes = ["ECB", "CBC"];
                string[] outputTypes = ["base64", "hex"];
                InputText = _encryption.Decrypt(OutputText, Key, paddings[PaddingIndex], 64, Iv, modes[EncryptModeIndex], outputTypes[OutputTypeIndex], GetSelectedKeyIvType());
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"解密失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private void GenerateNewKey()
        {
            Key = ToolMethod.GenerateSymmetricKey(64, GetSelectedKeyIvType());
            Iv = ToolMethod.GenerateSymmetricKey(64, GetSelectedKeyIvType());
        }

        private string GetSelectedKeyIvType() => KeyIvTypeIndex switch
        {
            0 => "text",
            1 => "base64",
            2 => "hex",
            _ => "text"
        };

        private bool ValidateSymmetricKeyLength(string keyStr, int expectedBytes, string keyIvType)
        {
            if (string.IsNullOrEmpty(keyStr))
            {
                _notificationService.ShowError("密钥字符串不能为空");
                return false;
            }
            byte[] key = Encoding.UTF8.GetBytes(keyStr);
            if (keyIvType == "hex")
            {
                key = Hex.Decode(keyStr);
            }
            if (keyIvType == "base64")
            {
                key = Convert.FromBase64String(keyStr);
            }
            if (key.Length != expectedBytes)
            {
                _notificationService.ShowError($"密钥字符串长度必须为{expectedBytes}字节");
                return false;
            }
            return true;
        }

        private bool ValidateSymmetricIvLength(string ivStr, int expectedBytes, string keyIvType)
        {
            if (string.IsNullOrEmpty(ivStr))
            {
                _notificationService.ShowError("IV字符串不能为空");
                return false;
            }
            byte[] iv = Encoding.UTF8.GetBytes(ivStr);
            if (keyIvType == "hex")
            {
                iv = Hex.Decode(ivStr);
            }
            if (keyIvType == "base64")
            {
                iv = Convert.FromBase64String(ivStr);
            }
            if (iv.Length != expectedBytes)
            {
                _notificationService.ShowError($"IV字符串长度必须为{expectedBytes}字节");
                return false;
            }
            return true;
        }
    }
}
