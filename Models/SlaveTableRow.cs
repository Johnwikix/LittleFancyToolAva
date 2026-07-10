using CommunityToolkit.Mvvm.ComponentModel;

namespace LittleFancyToolAva.Models
{
    public partial class SlaveTableRow : ObservableObject
    {
        [ObservableProperty]
        private string _address = string.Empty;

        [ObservableProperty]
        private string _value = string.Empty;

        [ObservableProperty]
        private bool _enabled;

        [ObservableProperty]
        private string _lastUpdate = string.Empty;
    }
}
