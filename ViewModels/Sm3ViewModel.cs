using CommunityToolkit.Mvvm.Input;
using Lang.Avalonia;
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
            DisplayTitle = LocalizationRegistry.Get("Hash.SM3_Title");
            DisplaySubtitle = LocalizationRegistry.Get("Hash.SM3_Subtitle");
            viewStateService.Register(this);

            I18nManager.Instance.CultureChanged += (_, _) =>
            {
                DisplayTitle = LocalizationRegistry.Get("Hash.SM3_Title");
                DisplaySubtitle = LocalizationRegistry.Get("Hash.SM3_Subtitle");
            };
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
