using Avalonia;
using Avalonia.Controls;

namespace FancyToolAva.Views.Controls
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
    }
}