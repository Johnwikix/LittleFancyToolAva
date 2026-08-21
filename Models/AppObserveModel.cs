using CommunityToolkit.Mvvm.ComponentModel;

namespace FancyToolAva.Models
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
