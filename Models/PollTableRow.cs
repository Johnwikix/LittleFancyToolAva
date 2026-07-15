using CommunityToolkit.Mvvm.ComponentModel;

namespace LittleFancyToolAva.Models
{
    public partial class PollTableRow : ObservableObject
    {
        public string Address
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string ValueDec
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string ValueHex
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string LastUpdate
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;
    }
}
