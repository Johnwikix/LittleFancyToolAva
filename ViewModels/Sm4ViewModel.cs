using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace LittleFancyToolAva.ViewModels
{
    public partial class Sm4ViewModel : ViewModelBase
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

        public Sm4ViewModel([FromKeyedServices("SM4")] IEncryptionSymmetric encryption, INotificationService notificationService)
        {
            _encryption = encryption;
            _notificationService = notificationService;
            Key = ToolMethod.GenerateSymmetricKey(128, "text");
            Iv = ToolMethod.GenerateSymmetricKey(128, "text");
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            try
            {
                string[] paddings = ["PKCS7", "ISO10126", "ZEROBYTE"];
                string[] modes = ["ECB", "CBC"];
                string[] outputTypes = ["base64", "hex"];
                OutputText = _encryption.Encrypt(InputText, Key, paddings[PaddingIndex], 128, Iv, modes[EncryptModeIndex], outputTypes[OutputTypeIndex], GetSelectedKeyIvType());
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
                string[] paddings = ["PKCS7", "ISO10126", "ZEROBYTE"];
                string[] modes = ["ECB", "CBC"];
                string[] outputTypes = ["base64", "hex"];
                InputText = _encryption.Decrypt(OutputText, Key, paddings[PaddingIndex], 128, Iv, modes[EncryptModeIndex], outputTypes[OutputTypeIndex], GetSelectedKeyIvType());
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"解密失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private void GenerateNewKey()
        {
            Key = ToolMethod.GenerateSymmetricKey(128, GetSelectedKeyIvType());
            Iv = ToolMethod.GenerateSymmetricKey(128, GetSelectedKeyIvType());
        }

        private string GetSelectedKeyIvType() => KeyIvTypeIndex switch
        {
            0 => "text",
            1 => "base64",
            2 => "hex",
            _ => "text"
        };
    }
}
