using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Utils;

namespace LittleFancyToolAva.ViewModels
{
    public partial class DesViewModel : ViewModelBase
    {
        private readonly IEncryptionSymmetric _encryption;
        [ObservableProperty] private string _inputText = string.Empty;
        [ObservableProperty] private string _outputText = string.Empty;
        [ObservableProperty] private string _key = string.Empty;
        [ObservableProperty] private string _iv = string.Empty;
        [ObservableProperty] private int _paddingIndex;
        [ObservableProperty] private int _encryptModeIndex;
        [ObservableProperty] private int _outputTypeIndex;
        [ObservableProperty] private int _keyIvTypeIndex;

        public DesViewModel(IEncryptionSymmetric encryption)
        {
            _encryption = encryption;
            Key = ToolMethod.GenerateSymmetricKey(64, "text");
            Iv = ToolMethod.GenerateSymmetricKey(64, "text");
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            string[] paddings = ["PKCS7", "Zeros", "None"];
            string[] modes = ["ECB", "CBC"];
            string[] outputTypes = ["base64", "hex"];
            OutputText = _encryption.Encrypt(InputText, Key, paddings[PaddingIndex], 64, Iv, modes[EncryptModeIndex], outputTypes[OutputTypeIndex], GetSelectedKeyIvType());
        }

        [RelayCommand]
        private void Decrypt()
        {
            if (string.IsNullOrEmpty(OutputText)) return;
            string[] paddings = ["PKCS7", "Zeros", "None"];
            string[] modes = ["ECB", "CBC"];
            string[] outputTypes = ["base64", "hex"];
            InputText = _encryption.Decrypt(OutputText, Key, paddings[PaddingIndex], 64, Iv, modes[EncryptModeIndex], outputTypes[OutputTypeIndex], GetSelectedKeyIvType());
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
    }
}
