using CommunityToolkit.Mvvm.ComponentModel;
using LittleFancyToolAva.Models;

namespace LittleFancyToolAva.ViewModels
{
    public partial class SettingsViewModel : ViewModelBase
    {
        public AppObserveModel App { get; }

        public SettingsViewModel(AppObserveModel app)
        {
            App = app;
        }
    }
}
