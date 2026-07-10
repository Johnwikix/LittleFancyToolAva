using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;

namespace LittleFancyToolAva.ViewModels
{
    public partial class Base64ViewModel : ViewModelBase
    {
        private readonly IEncryptionCode _encryption;
        [ObservableProperty] private string _inputText = string.Empty;
        [ObservableProperty] private string _outputText = string.Empty;

        public Base64ViewModel()
        {
            _encryption = new Base64Encryption();
        }

        [RelayCommand]
        private void Encode()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            OutputText = _encryption.Encrypt(InputText);
        }

        [RelayCommand]
        private void Decode()
        {
            if (string.IsNullOrEmpty(OutputText)) return;
            InputText = _encryption.Decrypt(OutputText);
        }
    }
}
