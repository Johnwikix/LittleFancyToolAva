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

        public string InputText
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string OutputText
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string Key
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string Iv
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public int PaddingIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int EncryptModeIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int OutputTypeIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int KeyIvTypeIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        public string DisplayTitle
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string DisplaySubtitle
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public virtual int KeyLengthIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        public virtual int[] KeyLengthOptions => [];

        public string[] Paddings { get; protected set; } = [];

        public virtual int KeyBitLength => KeyLengthOptions.Length > 0 ? KeyLengthOptions[KeyLengthIndex] : 0;

        public virtual bool IsKeyLengthSelectable => false;

        protected SymmetricCipherViewModelBase(IEncryptionSymmetric encryption, INotificationService notificationService)
        {
            _encryption = encryption;
            _notificationService = notificationService;
        }

        protected void GenerateSymmetricKey()
        {
            Key = ToolMethod.GenerateSymmetricKey(KeyBitLength, GetSelectedKeyIvType());
            Iv = ToolMethod.GenerateSymmetricKey(_encryption.IvBitLength, GetSelectedKeyIvType());
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
                OutputText = _encryption.Encrypt(InputText, Key, Paddings[PaddingIndex], _encryption.KeyBitLength, Iv, modes[EncryptModeIndex], outputTypes[OutputTypeIndex], GetSelectedKeyIvType());
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
                InputText = _encryption.Decrypt(OutputText, Key, Paddings[PaddingIndex], _encryption.KeyBitLength, Iv, modes[EncryptModeIndex], outputTypes[OutputTypeIndex], GetSelectedKeyIvType());
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
