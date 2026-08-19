using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using LittleFancyToolAva.ViewModels;

namespace LittleFancyToolAva.Views.Controls
{
    public partial class SymmetricCipherView : UserControl
    {
        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }
        public static readonly StyledProperty<string> TitleProperty =
            AvaloniaProperty.Register<SymmetricCipherView, string>(nameof(Title));

        public SymmetricCipherView()
        {
            InitializeComponent();
        }

        private void Padding_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox { SelectedIndex: -1 } cb && cb.Items.Count > 0 && DataContext is SymmetricCipherViewModelBase vm)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (cb.SelectedIndex == -1 && cb.Items.Count > 0)
                    {
                        cb.SelectedIndex = Math.Clamp(vm.PaddingIndex, 0, cb.Items.Count - 1);
                    }
                }, DispatcherPriority.Background);
            }
        }

        private void KeyLength_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox { SelectedIndex: -1 } cb && cb.Items.Count > 0 && DataContext is SymmetricCipherViewModelBase vm)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (cb.SelectedIndex == -1 && cb.Items.Count > 0)
                    {
                        cb.SelectedIndex = Math.Clamp(vm.KeyLengthIndex, 0, cb.Items.Count - 1);
                    }
                }, DispatcherPriority.Background);
            }
        }
    }
}
