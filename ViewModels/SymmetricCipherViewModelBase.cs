using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.Utils;

namespace LittleFancyToolAva.ViewModels
{
    public abstract partial class SymmetricCipherViewModelBase : ViewModelBase
    {
        protected readonly IEncryptionSymmetric _encryption;
        protected readonly INotificationService _notificationService;

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
        private string _displayTitle = string.Empty;

        [ObservableProperty]
        private string _displaySubtitle = string.Empty;

        [ObservableProperty]
        private int _keyLengthIndex;

        public string[] Paddings { get; protected set; } = [];

        public abstract int KeyBitLength { get; }

        protected SymmetricCipherViewModelBase(IEncryptionSymmetric encryption, INotificationService notificationService)
        {
            _encryption = encryption;
            _notificationService = notificationService;
        }

        protected void GenerateSymmetricKey()
        {
            Key = ToolMethod.GenerateSymmetricKey(KeyBitLength, GetSelectedKeyIvType());
            Iv = ToolMethod.GenerateSymmetricKey(128, GetSelectedKeyIvType());
        }

        protected string GetSelectedKeyIvType() => KeyIvTypeIndex switch
        {
            0 => "text",
            1 => "base64",
            2 => "hex",
            _ => "text"
        };

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            try
            {
                string[] modes = ["ECB", "CBC"];
                string[] outputTypes = ["base64", "hex"];
                OutputText = _encryption.Encrypt(InputText, Key, Paddings[PaddingIndex], KeyBitLength, Iv, modes[EncryptModeIndex], outputTypes[OutputTypeIndex], GetSelectedKeyIvType());
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
                string[] modes = ["ECB", "CBC"];
                string[] outputTypes = ["base64", "hex"];
                InputText = _encryption.Decrypt(OutputText, Key, Paddings[PaddingIndex], KeyBitLength, Iv, modes[EncryptModeIndex], outputTypes[OutputTypeIndex], GetSelectedKeyIvType());
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"解密失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private void GenerateNewKey()
        {
            GenerateSymmetricKey();
        }
    }
}
