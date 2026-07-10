using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using LittleFancyToolAva.ViewModels;
using System.Collections.ObjectModel;

namespace LittleFancyToolAva.Models
{
    public partial class PageNavigationItem : ObservableObject
    {
        public PageNavigationItem(string label, FASymbol icon)
        {
            _label = label;
            _iconSource = new FASymbolIconSource { Symbol = icon };
        }

        public PageNavigationItem(string label, FASymbol icon, ViewModelBase content)
            : this(label, icon)
        {
            _content = content;
        }

        [ObservableProperty]
        private string _label = string.Empty;

        [ObservableProperty]
        private FAIconSource _iconSource;

        [ObservableProperty]
        private ViewModelBase? _content;

        public ObservableCollection<PageNavigationItem> Children { get; } = new();

        public bool HasChildren => Children.Count > 0;
    }
}
