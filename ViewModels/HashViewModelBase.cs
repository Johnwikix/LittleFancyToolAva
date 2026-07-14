using CommunityToolkit.Mvvm.ComponentModel;
using LittleFancyToolAva.Algorithms;

namespace LittleFancyToolAva.ViewModels
{
    public abstract partial class HashViewModelBase : ViewModelBase
    {
        protected readonly IEncryptionAbstract _encryption;

        [ObservableProperty]
        private string _inputText = string.Empty;

        [ObservableProperty]
        private string _outputText = string.Empty;

        [ObservableProperty]
        private int _caseIndex;

        [ObservableProperty]
        private string _displayTitle = string.Empty;

        [ObservableProperty]
        private string _displaySubtitle = string.Empty;

        public string[] Cases { get; } = ["UPPER", "lower"];

        protected HashViewModelBase(IEncryptionAbstract encryption)
        {
            _encryption = encryption;
        }
    }
}
