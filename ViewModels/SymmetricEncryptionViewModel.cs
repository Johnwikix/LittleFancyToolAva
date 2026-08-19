using Avalonia.Threading;
using Lang.Avalonia;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LittleFancyToolAva.ViewModels
{
    public partial class SymmetricEncryptionViewModel : SymmetricCipherViewModelBase, IViewState
    {
        private sealed class AlgorithmOption
        {
            public required string Name { get; init; }
            public required IEncryptionSymmetric Encryption { get; init; }
            public required string[] Paddings { get; init; }
            public int[] KeyLengths { get; init; } = [];
        }

        private readonly AlgorithmOption[] _options;
        private readonly Dictionary<string, SymmetricCipherViewState> _algoStates = [];
        private AlgorithmOption _current;
        private bool _isRestoring;

        public override int[] KeyLengthOptions => _current.KeyLengths;

        public override bool IsKeyLengthSelectable => _current.KeyLengths.Length > 1;

        public override int KeyLengthIndex
        {
            get;
            set
            {
                var v = Math.Max(value, 0);
                if (SetProperty(ref field, v))
                {
                    GenerateSymmetricKey();
                }
                else if (v != value)
                {
                    OnPropertyChanged();
                }
            }
        }

        string IViewState.ViewName => "symmetricView";

        public SymmetricEncryptionViewModel(
            [FromKeyedServices("AES")] IEncryptionSymmetric aes,
            [FromKeyedServices("DES")] IEncryptionSymmetric des,
            [FromKeyedServices("SM4")] IEncryptionSymmetric sm4,
            INotificationService notificationService,
            IViewStateService viewStateService)
            : base(aes, notificationService)
        {
            _options =
            [
                new AlgorithmOption { Name = "AES", Encryption = aes, Paddings = ["PKCS7", "Zeros", "None"], KeyLengths = [128, 192, 256] },
                new AlgorithmOption { Name = "DES", Encryption = des, Paddings = ["PKCS7", "Zeros", "None"], KeyLengths = [64] },
                new AlgorithmOption { Name = "SM4", Encryption = sm4, Paddings = ["PKCS7", "ISO10126", "ZEROBYTE"], KeyLengths = [128] }
            ];
            Algorithms = _options.Select(o => o.Name).ToArray();
            _current = _options[0];
            Paddings = _current.Paddings;
            GenerateSymmetricKey();
            viewStateService.Register(this);
        }

        protected override (string TitleKey, string SubtitleKey) GetTitleKeys() =>
            ("Encrypt.Symmetric_Title", "Encrypt.Symmetric_Subtitle");

        protected override void OnAlgorithmChanged(int index)
        {
            if (index < 0 || index >= _options.Length) return;
            if (!_isRestoring)
            {
                SaveCurrentState();
            }
            _current = _options[index];
            _encryption = _current.Encryption;
            Paddings = _current.Paddings;
            OnPropertyChanged(nameof(KeyLengthOptions));
            OnPropertyChanged(nameof(IsKeyLengthSelectable));
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

        private void SaveCurrentState()
        {
            _algoStates[_current.Name] = new SymmetricCipherViewState
            {
                InputText = InputText,
                OutputText = OutputText,
                Key = Key,
                Iv = Iv,
                PaddingIndex = PaddingIndex,
                EncryptModeIndex = EncryptModeIndex,
                OutputTypeIndex = OutputTypeIndex,
                KeyIvTypeIndex = KeyIvTypeIndex,
                KeyLengthIndex = KeyLengthIndex
            };
        }

        private void RestoreCurrentState()
        {
            if (_algoStates.TryGetValue(_current.Name, out var s))
            {
                KeyLengthIndex = Math.Clamp(s.KeyLengthIndex, 0, Math.Max(_current.KeyLengths.Length - 1, 0));
                PaddingIndex = Math.Clamp(s.PaddingIndex, 0, Math.Max(_current.Paddings.Length - 1, 0));
                EncryptModeIndex = s.EncryptModeIndex;
                OutputTypeIndex = s.OutputTypeIndex;
                KeyIvTypeIndex = s.KeyIvTypeIndex;
                Key = s.Key;
                Iv = s.Iv;
                InputText = s.InputText;
                OutputText = s.OutputText;
            }
            else
            {
                KeyLengthIndex = 0;
                PaddingIndex = 0;
                GenerateSymmetricKey();
            }
        }

        object IViewState.CaptureState()
        {
            SaveCurrentState();
            return new SymmetricEncryptionViewState
            {
                AlgorithmIndex = AlgorithmIndex,
                AlgoStates = new Dictionary<string, SymmetricCipherViewState>(_algoStates)
            };
        }

        void IViewState.RestoreState(object state)
        {
            if (state is SymmetricEncryptionViewState s)
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