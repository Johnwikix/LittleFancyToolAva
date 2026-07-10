using CommunityToolkit.Mvvm.ComponentModel;

namespace LittleFancyToolAva.Models
{
    public partial class AppObserveModel : ObservableObject
    {
        [ObservableProperty]
        private AppPreferences _preferences = new();
    }
}
