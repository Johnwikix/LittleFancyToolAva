using Avalonia.Controls;
using Avalonia.Input;

namespace LittleFancyToolAva.Views
{
    public partial class HomeView : UserControl
    {
        public HomeView()
        {
            InitializeComponent();
        }

        private void Image_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is ViewModels.HomeViewModel vm)
            {
                vm.ToggleRotationCommand.Execute(null);
            }
        }
    }
}
