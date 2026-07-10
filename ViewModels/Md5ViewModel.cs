using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;

namespace LittleFancyToolAva.ViewModels
{
    public partial class Md5ViewModel : ViewModelBase
    {
        private readonly IEncryptionAbstract _encryption;
        [ObservableProperty] private string _inputText = string.Empty;
        [ObservableProperty] private string _outputText = string.Empty;
        [ObservableProperty] private int _caseIndex;
        [ObservableProperty] private int _outputLengthIndex;

        public Md5ViewModel()
        {
            _encryption = new Md5Encryption();
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            string[] cases = ["UPPER", "lower"];
            int[] lengths = [32, 16];
            OutputText = _encryption.Encrypt(InputText, cases[CaseIndex], lengths[OutputLengthIndex]);
        }
    }
}
