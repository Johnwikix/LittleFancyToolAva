using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels
{
    public partial class ShaViewModel : HashViewModelBase, IViewState
    {
        [ObservableProperty]
        private int _modeIndex;

        private readonly string[] _modes = ["SHA1", "SHA256", "SHA384", "SHA512"];

        string IViewState.ViewName => "shaView";

        public ShaViewModel(IViewStateService viewStateService) : base(new SHAEncrpytion())
        {
            DisplayTitle = "SHA 哈希";
            DisplaySubtitle = "SHA-1 / SHA-256 / SHA-384 / SHA-512 安全散列算法";
            viewStateService.Register(this);
        }

        object IViewState.CaptureState() => new ShaViewState
        {
            InputText = InputText,
            CaseIndex = CaseIndex,
            ModeIndex = ModeIndex
        };

        void IViewState.RestoreState(object state)
        {
            if (state is ShaViewState s)
            {
                InputText = s.InputText;
                CaseIndex = s.CaseIndex;
                ModeIndex = s.ModeIndex;
            }
        }

        [RelayCommand]
        private void Encrypt()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            OutputText = _encryption.Encrypt(InputText, Cases[CaseIndex], 0, _modes[ModeIndex]);
        }
    }
}
