using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using LittleFancyToolAva.ViewModels;

namespace LittleFancyToolAva.Models
{
    public partial class PageNavigationItem : ObservableObject
    {
        public PageNavigationItem(string label, FASymbol icon, ViewModelBase content)
        {
            _label = label;
            _iconSource = new FASymbolIconSource { Symbol = icon };
            _content = content;
        }

        [ObservableProperty]
        private string _label = string.Empty;

        [ObservableProperty]
        private FAIconSource _iconSource;

        [ObservableProperty]
        private ViewModelBase _content;
    }
}
