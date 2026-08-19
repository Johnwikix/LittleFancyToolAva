using Avalonia.Controls;
using Avalonia.Threading;
using LittleFancyToolAva.ViewModels;

namespace LittleFancyToolAva.Views.Controls
{
    public partial class HashCipherView : UserControl
    {
        public HashCipherView()
        {
            InitializeComponent();
        }

        private void OutputLength_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox { SelectedIndex: -1 } cb && cb.Items.Count > 0 && DataContext is HashEncryptionViewModel vm)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (cb.SelectedIndex == -1 && cb.Items.Count > 0)
                    {
                        cb.SelectedIndex = Math.Clamp(vm.OutputLengthIndex, 0, cb.Items.Count - 1);
                    }
                }, DispatcherPriority.Background);
            }
        }

        private void Mode_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox { SelectedIndex: -1 } cb && cb.Items.Count > 0 && DataContext is HashEncryptionViewModel vm)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (cb.SelectedIndex == -1 && cb.Items.Count > 0)
                    {
                        cb.SelectedIndex = Math.Clamp(vm.ModeIndex, 0, cb.Items.Count - 1);
                    }
                }, DispatcherPriority.Background);
            }
        }
    }
}