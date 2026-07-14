using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels
{
    public partial class Sm3ViewModel : HashViewModelBase, IViewState
    {
        string IViewState.ViewName => "sm3View";

        public Sm3ViewModel(IViewStateService viewStateService) : base(new SM3Encryption())
        {
            DisplayTitle = "SM3 哈希";
            DisplaySubtitle = "国密 SM3 密码杂凑算法";
            viewStateService.Register(this);
        }

        object IViewState.CaptureState() => new HashViewState
        {
            InputText = InputText,
            CaseIndex = CaseIndex
        };

        void IViewState.RestoreState(object state)
        {
            if (state is HashViewState s)
            {
                InputText = s.InputText;
                CaseIndex = s.CaseIndex;
            }
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            OutputText = _encryption.Encrypt(InputText, Cases[CaseIndex], 0);
        }
    }
}
