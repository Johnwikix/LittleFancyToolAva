using CommunityToolkit.Mvvm.ComponentModel;
using LittleFancyToolAva.Algorithms;

namespace LittleFancyToolAva.ViewModels
{
    public abstract partial class AsymmetricCipherViewModelBase : ViewModelBase
    {
        protected readonly IEncryptionAsymmetric _encryption;

        [ObservableProperty]
        private string _inputText = string.Empty;

        [ObservableProperty]
        private string _outputText = string.Empty;

        [ObservableProperty]
        private string _publicKey = string.Empty;

        [ObservableProperty]
        private string _privateKey = string.Empty;

        [ObservableProperty]
        private string _displayTitle = string.Empty;

        [ObservableProperty]
        private string _displaySubtitle = string.Empty;

        protected AsymmetricCipherViewModelBase(IEncryptionAsymmetric encryption)
        {
            _encryption = encryption;
        }
    }
}
