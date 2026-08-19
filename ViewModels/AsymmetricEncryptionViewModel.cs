using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lang.Avalonia;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels
{
    public partial class AsymmetricEncryptionViewModel : AsymmetricCipherViewModelBase, IViewState
    {
        private enum AlgorithmKind
        {
            Rsa,
            Sm2
        }

        private sealed class AlgorithmOption
        {
            public required string Name { get; init; }
            public required AlgorithmKind Kind { get; init; }
            public required IEncryptionAsymmetric Encryption { get; init; }
            public string[] Paddings { get; init; } = [];
            public int[] KeyLengths { get; init; } = [];
            public string[] KeyFormats { get; init; } = [];
            public string[] Modes { get; init; } = [];
        }

        private readonly AlgorithmOption[] _options;
        private readonly Dictionary<string, AsymmetricCipherViewState> _algoStates = [];
        private AlgorithmOption _current;
        private bool _isRestoring;

        public string[] Algorithms
        {
            get;
            private set => SetProperty(ref field, value);
        } = [];

        public int AlgorithmIndex
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
            private set => SetProperty(ref field, value);
        } = [];

        public int[] KeyLengths
        {
            get;
            private set => SetProperty(ref field, value);
        } = [];

        public string[] KeyFormats
        {
            get;
            private set => SetProperty(ref field, value);
        } = [];

        public string[] Modes
        {
            get;
            private set => SetProperty(ref field, value);
        } = [];

        public int PaddingIndex
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

        public int KeyLengthIndex
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

        public int KeyFormatIndex
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

        public int ModeIndex
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

        public bool IsPaddingVisible
        {
            get;
            private set => SetProperty(ref field, value);
        }

        public bool IsKeyLengthVisible
        {
            get;
            private set => SetProperty(ref field, value);
        }

        public bool IsKeyFormatVisible
        {
            get;
            private set => SetProperty(ref field, value);
        }

        public bool IsModeVisible
        {
            get;
            private set => SetProperty(ref field, value);
        }

        string IViewState.ViewName => "asymmetricView";

        public AsymmetricEncryptionViewModel(IViewStateService viewStateService)
            : base(new RSAEncryption())
        {
            _options =
            [
                new AlgorithmOption
                {
                    Name = "RSA",
                    Kind = AlgorithmKind.Rsa,
                    Encryption = new RSAEncryption(),
                    Paddings = ["Pkcs1", "OaepSHA1", "OaepSHA256", "OaepSHA384", "OaepSHA512"],
                    KeyLengths = [1024, 2048, 4096],
                    KeyFormats = ["PKCS#1", "PKCS#8"]
                },
                new AlgorithmOption
                {
                    Name = "SM2",
                    Kind = AlgorithmKind.Sm2,
                    Encryption = new SM2Encryption(),
                    Modes = ["C1C2C3", "C1C3C2"]
                }
            ];
            Algorithms = _options.Select(o => o.Name).ToArray();
            _current = _options[0];
            ApplyCurrentOptions();
            GenerateKeyPair();
            viewStateService.Register(this);

            I18nManager.Instance.CultureChanged += (_, _) =>
            {
                DisplayTitle = LocalizationRegistry.Get("Encrypt.Asymmetric_Title");
                DisplaySubtitle = LocalizationRegistry.Get("Encrypt.Asymmetric_Subtitle");
            };
        }

        private void OnAlgorithmChanged(int index)
        {
            if (index < 0 || index >= _options.Length) return;
            if (!_isRestoring)
            {
                SaveCurrentState();
            }
            _current = _options[index];
            _encryption = _current.Encryption;
            ApplyCurrentOptions();
            PostRestore(_current, 3);
        }

        private void PostRestore(AlgorithmOption option, int attempts)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_current != option) return;
                RestoreCurrentState();
                if (attempts > 1)
                {
                    PostRestore(option, attempts - 1);
                }
            }, DispatcherPriority.Background);
        }

        private void ApplyCurrentOptions()
        {
            Paddings = _current.Paddings;
            KeyLengths = _current.KeyLengths;
            KeyFormats = _current.KeyFormats;
            Modes = _current.Modes;
            IsPaddingVisible = _current.Paddings.Length > 0;
            IsKeyLengthVisible = _current.KeyLengths.Length > 0;
            IsKeyFormatVisible = _current.KeyFormats.Length > 0;
            IsModeVisible = _current.Modes.Length > 0;
        }

        private void SaveCurrentState()
        {
            _algoStates[_current.Name] = new AsymmetricCipherViewState
            {
                InputText = InputText,
                OutputText = OutputText,
                PublicKey = PublicKey,
                PrivateKey = PrivateKey,
                PaddingIndex = PaddingIndex,
                KeyLengthIndex = KeyLengthIndex,
                KeyFormatIndex = KeyFormatIndex,
                ModeIndex = ModeIndex
            };
        }

        private void RestoreCurrentState()
        {
            if (_algoStates.TryGetValue(_current.Name, out var s))
            {
                PaddingIndex = Math.Clamp(s.PaddingIndex, 0, Math.Max(Paddings.Length - 1, 0));
                KeyLengthIndex = Math.Clamp(s.KeyLengthIndex, 0, Math.Max(KeyLengths.Length - 1, 0));
                KeyFormatIndex = Math.Clamp(s.KeyFormatIndex, 0, Math.Max(KeyFormats.Length - 1, 0));
                ModeIndex = Math.Clamp(s.ModeIndex, 0, Math.Max(Modes.Length - 1, 0));
                PublicKey = s.PublicKey;
                PrivateKey = s.PrivateKey;
                InputText = s.InputText;
                OutputText = s.OutputText;
            }
            else
            {
                PaddingIndex = 0;
                KeyLengthIndex = 0;
                KeyFormatIndex = 0;
                ModeIndex = 0;
                GenerateKeyPair();
            }
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            if (_current.Kind == AlgorithmKind.Rsa)
            {
                OutputText = _encryption.Encrypt(InputText, PublicKey, Paddings[PaddingIndex], KeyLengths[KeyLengthIndex]);
            }
            else
            {
                OutputText = _encryption.Encrypt(InputText, PublicKey, Modes[ModeIndex]);
            }
        }

        [RelayCommand]
        private void Decrypt()
        {
            if (string.IsNullOrEmpty(OutputText)) return;
            if (_current.Kind == AlgorithmKind.Rsa)
            {
                InputText = _encryption.Decrypt(OutputText, PrivateKey, Paddings[PaddingIndex], KeyLengths[KeyLengthIndex]);
            }
            else
            {
                InputText = _encryption.Decrypt(OutputText, PrivateKey, Modes[ModeIndex]);
            }
        }

        [RelayCommand]
        private void GenerateKeyPair()
        {
            if (_current.Kind == AlgorithmKind.Rsa)
            {
                var (pub, priv) = _encryption.GenerateKeyPair(KeyLengths[KeyLengthIndex], KeyFormats[KeyFormatIndex]);
                PublicKey = pub;
                PrivateKey = priv;
            }
            else
            {
                var (pub, priv) = _encryption.GenerateKeyPair();
                PublicKey = pub;
                PrivateKey = priv;
            }
        }

        object IViewState.CaptureState()
        {
            SaveCurrentState();
            return new AsymmetricEncryptionViewState
            {
                AlgorithmIndex = AlgorithmIndex,
                AlgoStates = new Dictionary<string, AsymmetricCipherViewState>(_algoStates)
            };
        }

        void IViewState.RestoreState(object state)
        {
            if (state is AsymmetricEncryptionViewState s)
            {
                _algoStates.Clear();
                foreach (var kv in s.AlgoStates)
                {
                    _algoStates[kv.Key] = kv.Value;
                }
                _isRestoring = true;
                try
                {
                    AlgorithmIndex = Math.Clamp(s.AlgorithmIndex, 0, _options.Length - 1);
                }
                finally
                {
                    _isRestoring = false;
                }
                RestoreCurrentState();
            }
        }
    }
}