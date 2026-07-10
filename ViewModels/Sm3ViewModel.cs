using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;

namespace LittleFancyToolAva.ViewModels
{
    public partial class Sm3ViewModel : ViewModelBase
    {
        private readonly IEncryptionAbstract _encryption;
        [ObservableProperty] private string _inputText = string.Empty;
        [ObservableProperty] private string _outputText = string.Empty;
        [ObservableProperty] private int _caseIndex;

        public Sm3ViewModel()
        {
            _encryption = new SM3Encryption();
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            string[] cases = ["UPPER", "lower"];
            OutputText = _encryption.Encrypt(InputText, cases[CaseIndex], 0);
        }
    }
}
