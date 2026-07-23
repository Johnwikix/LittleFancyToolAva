using Avalonia.Controls;
using LittleFancyToolAva.Models;

namespace LittleFancyToolAva.Views
{
    public partial class HomeView : UserControl
    {
        public HomeView()
        {
            InitializeComponent();
        }

        private void OnToolSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is PageNavigationItem item)
            {
                if (DataContext is ViewModels.HomeViewModel vm)
                    vm.NavigateToToolCommand.Execute(item);
                if (sender is ListBox lb)
                    lb.SelectedItem = null;
            }
        }
    }
}
