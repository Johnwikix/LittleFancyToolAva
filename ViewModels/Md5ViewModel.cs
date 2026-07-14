using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;

namespace LittleFancyToolAva.ViewModels
{
    public partial class Md5ViewModel : HashViewModelBase
    {
        [ObservableProperty]
        private int _outputLengthIndex;

        private readonly int[] _lengths = [32, 16];

        public Md5ViewModel() : base(new Md5Encryption())
        {
            DisplayTitle = "MD5 哈希";
            DisplaySubtitle = "MD5 消息摘要算法 (128-bit)";
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            OutputText = _encryption.Encrypt(InputText, Cases[CaseIndex], _lengths[OutputLengthIndex]);
        }
    }
}
