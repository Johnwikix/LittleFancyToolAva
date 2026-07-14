using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;

namespace LittleFancyToolAva.ViewModels
{
    public partial class ShaViewModel : HashViewModelBase
    {
        [ObservableProperty]
        private int _modeIndex;

        private readonly string[] _modes = ["SHA1", "SHA256", "SHA384", "SHA512"];

        public ShaViewModel() : base(new SHAEncrpytion())
        {
            DisplayTitle = "SHA 哈希";
            DisplaySubtitle = "SHA-1 / SHA-256 / SHA-384 / SHA-512 安全散列算法";
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            OutputText = _encryption.Encrypt(InputText, Cases[CaseIndex], 0, _modes[ModeIndex]);
        }
    }
}
