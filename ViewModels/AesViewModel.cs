using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace LittleFancyToolAva.ViewModels
{
    public partial class AesViewModel : ViewModelBase
    {
        private readonly IEncryptionSymmetric _encryption;
        private readonly INotificationService _notificationService;

        [ObservableProperty]
        private string _inputText = string.Empty;

        [ObservableProperty]
        private string _outputText = string.Empty;

        [ObservableProperty]
        private string _key = string.Empty;

        [ObservableProperty]
        private string _iv = string.Empty;

        [ObservableProperty]
        private int _paddingIndex;

        [ObservableProperty]
        private int _encryptModeIndex;

        [ObservableProperty]
        private int _outputTypeIndex;

        [ObservableProperty]
        private int _keyIvTypeIndex;

        [ObservableProperty]
        private int _keyLengthIndex;

        public AesViewModel([FromKeyedServices("AES")] IEncryptionSymmetric encryption, INotificationService notificationService)
        {
            _encryption = encryption;
            _notificationService = notificationService;
            GenerateKey();
        }

        private void GenerateKey()
        {
            int[] keyLengths = [128, 192, 256];
            int bitLen = keyLengths[KeyLengthIndex >= 0 && KeyLengthIndex < keyLengths.Length ? KeyLengthIndex : 0];
            string keyIvType = GetSelectedKeyIvType();
            Key = ToolMethod.GenerateSymmetricKey(bitLen, keyIvType);
            Iv = ToolMethod.GenerateSymmetricKey(128, keyIvType);
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            try
            {
                string[] paddings = ["PKCS7", "Zeros", "None"];
                string[] modes = ["ECB", "CBC"];
                string[] outputTypes = ["base64", "hex"];
                OutputText = _encryption.Encrypt(
                    InputText, Key, paddings[PaddingIndex], 128, Iv,
                    modes[EncryptModeIndex], outputTypes[OutputTypeIndex],
                    GetSelectedKeyIvType());
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
            try
            {
                string[] paddings = ["PKCS7", "Zeros", "None"];
                string[] modes = ["ECB", "CBC"];
                string[] outputTypes = ["base64", "hex"];
                InputText = _encryption.Decrypt(
                    OutputText, Key, paddings[PaddingIndex], 128, Iv,
                    modes[EncryptModeIndex], outputTypes[OutputTypeIndex],
                    GetSelectedKeyIvType());
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"解密失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private void GenerateNewKey()
        {
            GenerateKey();
        }

        private string GetSelectedKeyIvType()
        {
            return KeyIvTypeIndex switch
            {
                0 => "text",
                1 => "base64",
                2 => "hex",
                _ => "text"
            };
        }
    }
}
