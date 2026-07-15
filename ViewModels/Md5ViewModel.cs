using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels
{
    public partial class Md5ViewModel : HashViewModelBase, IViewState
    {
        public int OutputLengthIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        private readonly int[] _lengths = [32, 16];

        string IViewState.ViewName => "md5View";

        public Md5ViewModel(IViewStateService viewStateService) : base(new Md5Encryption())
        {
            DisplayTitle = "MD5 哈希";
            DisplaySubtitle = "MD5 消息摘要算法 (128-bit)";
            viewStateService.Register(this);
        }

        object IViewState.CaptureState() => new Md5ViewState
        {
            InputText = InputText,
            CaseIndex = CaseIndex,
            OutputLengthIndex = OutputLengthIndex
        };

        void IViewState.RestoreState(object state)
        {
            if (state is Md5ViewState s)
            {
                InputText = s.InputText;
                CaseIndex = s.CaseIndex;
                OutputLengthIndex = s.OutputLengthIndex;
            }
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            OutputText = _encryption.Encrypt(InputText, Cases[CaseIndex], _lengths[OutputLengthIndex]);
        }
    }
}
