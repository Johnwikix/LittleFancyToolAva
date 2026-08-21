using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lang.Avalonia;
using FancyToolAva.Algorithms;
using FancyToolAva.Services;
using FancyToolAva.Utils;

namespace FancyToolAva.ViewModels
{
    public abstract partial class SymmetricCipherViewModelBase : ViewModelBase
    {
        protected IEncryptionSymmetric _encryption;
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

        public string? SelectedPadding
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(PaddingIndex));
                }
            }
        }

        public int PaddingIndex => SelectedPadding is null ? 0 : Math.Max(Array.IndexOf(Paddings, SelectedPadding), 0);

        public int EncryptModeIndex
        {
            get;
            set
            {
                var v = Math.Max(value, 0);
                if (!SetProperty(ref field, v) && v != value)
                {
                    OnPropertyChanged();
                }
            }
        }

        public int OutputTypeIndex
        {
            get;
            set
            {
                var v = Math.Max(value, 0);
                if (!SetProperty(ref field, v) && v != value)
                {
                    OnPropertyChanged();
                }
            }
        }

        public int KeyIvTypeIndex
        {
            get;
            set
            {
                var v = Math.Max(value, 0);
                if (!SetProperty(ref field, v) && v != value)
                {
                    OnPropertyChanged();
                }
            }
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

        public virtual int? SelectedKeyLength
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(KeyLengthIndex));
                }
            }
        }

        public int KeyLengthIndex => SelectedKeyLength is null ? 0 : Math.Max(Array.IndexOf(KeyLengthOptions, SelectedKeyLength.Value), 0);

        public virtual int[] KeyLengthOptions => [];

        public string[] Algorithms
        {
            get;
            protected set => SetProperty(ref field, value);
        } = [];

        public virtual int AlgorithmIndex
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnAlgorithmChanged(value);
                }
            }
        }

        public string[] Paddings
        {
            get;
            protected set => SetProperty(ref field, value);
        } = [];

        public virtual int KeyBitLength => KeyLengthOptions.Length > 0 ? KeyLengthOptions[Math.Clamp(KeyLengthIndex, 0, KeyLengthOptions.Length - 1)] : 0;

        public virtual bool IsKeyLengthSelectable => false;

        protected SymmetricCipherViewModelBase(IEncryptionSymmetric encryption, INotificationService notificationService)
        {
            _encryption = encryption;
            _notificationService = notificationService;

            I18nManager.Instance.CultureChanged += OnCultureChanged;
        }

        protected abstract (string TitleKey, string SubtitleKey) GetTitleKeys();

        protected virtual void OnAlgorithmChanged(int index)
        {
        }

        private void OnCultureChanged(object? sender, EventArgs e)
        {
            var (titleKey, subtitleKey) = GetTitleKeys();
            DisplayTitle = LocalizationRegistry.Get(titleKey);
            DisplaySubtitle = LocalizationRegistry.Get(subtitleKey);
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
                _notificationService.ShowError(LocalizationRegistry.Get("Encrypt.Msg_EncryptFail", ex.Message));
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
                _notificationService.ShowError(LocalizationRegistry.Get("Encrypt.Msg_DecryptFail", ex.Message));
            }
        }

        [RelayCommand]
        private void GenerateNewKey()
        {
            GenerateSymmetricKey();
        }
    }
}
