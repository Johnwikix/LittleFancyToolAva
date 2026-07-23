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
            Label = label;
            IconSource = new FASymbolIconSource { Symbol = icon };
        }

        public PageNavigationItem(string label, FASymbol icon, ViewModelBase content)
            : this(label, icon)
        {
            Content = content;
        }

        public string Label
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string Description
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public FAIconSource IconSource
        {
            get;
            set => SetProperty(ref field, value);
        }

        public ViewModelBase? Content
        {
            get;
            set => SetProperty(ref field, value);
        }

        public ObservableCollection<PageNavigationItem> Children { get; } = new();

        public bool HasChildren => Children.Count > 0;

        public bool IsExpanded
        {
            get;
            set => SetProperty(ref field, value);
        }
    }
}
