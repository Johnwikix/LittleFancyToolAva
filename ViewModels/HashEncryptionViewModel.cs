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
    public partial class HashEncryptionViewModel : HashViewModelBase, IViewState
    {
        private enum AlgorithmKind
        {
            Md5,
            Sha,
            Sm3
        }

        private sealed class AlgorithmOption
        {
            public required string Name { get; init; }
            public required AlgorithmKind Kind { get; init; }
            public required IEncryptionAbstract Encryption { get; init; }
            public string[] Modes { get; init; } = [];
            public int[] OutputLengths { get; init; } = [];
        }

        private readonly AlgorithmOption[] _options;
        private readonly Dictionary<string, HashViewState> _algoStates = [];
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

        public string[] Modes
        {
            get;
            private set => SetProperty(ref field, value);
        } = [];

        public int[] OutputLengths
        {
            get;
            private set => SetProperty(ref field, value);
        } = [];

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

        public int OutputLengthIndex
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

        public bool IsModeVisible
        {
            get;
            private set => SetProperty(ref field, value);
        }

        public bool IsOutputLengthVisible
        {
            get;
            private set => SetProperty(ref field, value);
        }

        string IViewState.ViewName => "hashView";

        public HashEncryptionViewModel(IViewStateService viewStateService)
            : base(new Md5Encryption())
        {
            _options =
            [
                new AlgorithmOption { Name = "MD5", Kind = AlgorithmKind.Md5, Encryption = new Md5Encryption(), OutputLengths = [32, 16] },
                new AlgorithmOption { Name = "SHA", Kind = AlgorithmKind.Sha, Encryption = new SHAEncrpytion(), Modes = ["SHA1", "SHA256", "SHA384", "SHA512"] },
                new AlgorithmOption { Name = "SM3", Kind = AlgorithmKind.Sm3, Encryption = new SM3Encryption() }
            ];
            Algorithms = _options.Select(o => o.Name).ToArray();
            _current = _options[0];
            ApplyCurrentOptions();
            viewStateService.Register(this);

            I18nManager.Instance.CultureChanged += (_, _) =>
            {
                DisplayTitle = LocalizationRegistry.Get("Hash.Hash_Title");
                DisplaySubtitle = LocalizationRegistry.Get("Hash.Hash_Subtitle");
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
            Modes = _current.Modes;
            OutputLengths = _current.OutputLengths;
            IsModeVisible = _current.Modes.Length > 0;
            IsOutputLengthVisible = _current.OutputLengths.Length > 0;
        }

        private void SaveCurrentState()
        {
            _algoStates[_current.Name] = new HashViewState
            {
                InputText = InputText,
                OutputText = OutputText,
                CaseIndex = CaseIndex,
                ModeIndex = ModeIndex,
                OutputLengthIndex = OutputLengthIndex
            };
        }

        private void RestoreCurrentState()
        {
            if (_algoStates.TryGetValue(_current.Name, out var s))
            {
                CaseIndex = s.CaseIndex;
                ModeIndex = Math.Clamp(s.ModeIndex, 0, Math.Max(Modes.Length - 1, 0));
                OutputLengthIndex = Math.Clamp(s.OutputLengthIndex, 0, Math.Max(OutputLengths.Length - 1, 0));
                InputText = s.InputText;
                OutputText = s.OutputText;
            }
            else
            {
                CaseIndex = 0;
                ModeIndex = 0;
                OutputLengthIndex = 0;
            }
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            OutputText = _current.Kind switch
            {
                AlgorithmKind.Md5 => _encryption.Encrypt(InputText, Cases[CaseIndex], OutputLengths[OutputLengthIndex]),
                AlgorithmKind.Sha => _encryption.Encrypt(InputText, Cases[CaseIndex], 0, Modes[ModeIndex]),
                _ => _encryption.Encrypt(InputText, Cases[CaseIndex], 0)
            };
        }

        object IViewState.CaptureState()
        {
            SaveCurrentState();
            return new HashEncryptionViewState
            {
                AlgorithmIndex = AlgorithmIndex,
                AlgoStates = new Dictionary<string, HashViewState>(_algoStates)
            };
        }

        void IViewState.RestoreState(object state)
        {
            if (state is HashEncryptionViewState s)
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