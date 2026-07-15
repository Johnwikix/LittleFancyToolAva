using CommunityToolkit.Mvvm.ComponentModel;
using LittleFancyToolAva.Algorithms;

namespace LittleFancyToolAva.ViewModels
{
    public abstract partial class AsymmetricCipherViewModelBase : ViewModelBase
    {
        protected readonly IEncryptionAsymmetric _encryption;

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

        public string PublicKey
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string PrivateKey
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

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

        protected AsymmetricCipherViewModelBase(IEncryptionAsymmetric encryption)
        {
            _encryption = encryption;
        }
    }
}
