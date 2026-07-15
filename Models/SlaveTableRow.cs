using CommunityToolkit.Mvvm.ComponentModel;

namespace LittleFancyToolAva.Models
{
    public partial class SlaveTableRow : ObservableObject
    {
        public string Address
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string Value
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public bool Enabled
        {
            get;
            set => SetProperty(ref field, value);
        }

        public string LastUpdate
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;
    }
}
