using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;

namespace LittleFancyToolAva.ViewModels
{
    public partial class Sm3ViewModel : HashViewModelBase
    {
        public Sm3ViewModel() : base(new SM3Encryption())
        {
            DisplayTitle = "SM3 哈希";
            DisplaySubtitle = "国密 SM3 密码杂凑算法";
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            OutputText = _encryption.Encrypt(InputText, Cases[CaseIndex], 0);
        }
    }
}
