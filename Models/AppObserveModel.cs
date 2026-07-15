using CommunityToolkit.Mvvm.ComponentModel;

namespace LittleFancyToolAva.Models
{
    public partial class AppObserveModel : ObservableObject
    {
        public AppPreferences Preferences
        {
            get;
            set => SetProperty(ref field, value);
        } = new();
    }
}
