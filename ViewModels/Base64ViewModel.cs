using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Algorithms;
using LittleFancyToolAva.Algorithms.Encryption;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels
{
    public partial class Base64ViewModel : ViewModelBase, IViewState
    {
        private readonly IEncryptionCode _encryption;
        public string InputText
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string OutputText
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        string IViewState.ViewName => "base64View";

        public Base64ViewModel(IViewStateService viewStateService)
        {
            _encryption = new Base64Encryption();
            viewStateService.Register(this);
        }

        object IViewState.CaptureState() => new Base64ViewState
        {
            InputText = InputText
        };

        void IViewState.RestoreState(object state)
        {
            if (state is Base64ViewState s)
            {
                InputText = s.InputText;
            }
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
