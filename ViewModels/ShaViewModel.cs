using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;

namespace LittleFancyToolAva.ViewModels
{
    public partial class ShaViewModel : ViewModelBase
    {
        private readonly IEncryptionAbstract _encryption;
        [ObservableProperty] private string _inputText = string.Empty;
        [ObservableProperty] private string _outputText = string.Empty;
        [ObservableProperty] private int _caseIndex;
        [ObservableProperty] private int _modeIndex;

        public ShaViewModel()
        {
            _encryption = new SHAEncrpytion();
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            string[] cases = ["UPPER", "lower"];
            string[] modes = ["SHA1", "SHA256", "SHA384", "SHA512"];
            OutputText = _encryption.Encrypt(InputText, cases[CaseIndex], 0, modes[ModeIndex]);
        }
    }
}
