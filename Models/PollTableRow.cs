using CommunityToolkit.Mvvm.ComponentModel;

namespace LittleFancyToolAva.Models
{
    public partial class PollTableRow : ObservableObject
    {
        [ObservableProperty]
        private string _address = string.Empty;

        [ObservableProperty]
        private string _valueDec = string.Empty;

        [ObservableProperty]
        private string _valueHex = string.Empty;

        [ObservableProperty]
        private string _lastUpdate = string.Empty;
    }
}
