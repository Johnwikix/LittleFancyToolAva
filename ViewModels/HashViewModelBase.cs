using CommunityToolkit.Mvvm.ComponentModel;
using LittleFancyToolAva.Algorithms;

namespace LittleFancyToolAva.ViewModels
{
    public abstract partial class HashViewModelBase : ViewModelBase
    {
        protected readonly IEncryptionAbstract _encryption;

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

        public int CaseIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        public string DisplayTitle
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string DisplaySubtitle
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string[] Cases { get; } = ["UPPER", "lower"];

        protected HashViewModelBase(IEncryptionAbstract encryption)
        {
            _encryption = encryption;
        }
    }
}
