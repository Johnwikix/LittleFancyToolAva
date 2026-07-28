using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lang.Avalonia;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels
{
    public partial class ShaViewModel : HashViewModelBase, IViewState
    {
        public int ModeIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        private readonly string[] _modes = ["SHA1", "SHA256", "SHA384", "SHA512"];

        string IViewState.ViewName => "shaView";

        public ShaViewModel(IViewStateService viewStateService) : base(new SHAEncrpytion())
        {
            DisplayTitle = LocalizationRegistry.Get("Hash.SHA_Title");
            DisplaySubtitle = LocalizationRegistry.Get("Hash.SHA_Subtitle");
            viewStateService.Register(this);

            I18nManager.Instance.CultureChanged += (_, _) =>
            {
                DisplayTitle = LocalizationRegistry.Get("Hash.SHA_Title");
                DisplaySubtitle = LocalizationRegistry.Get("Hash.SHA_Subtitle");
            };
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
