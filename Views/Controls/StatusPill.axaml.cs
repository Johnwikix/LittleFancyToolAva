using Avalonia;
using Avalonia.Controls;
using FancyToolAva.Models;

namespace FancyToolAva.Views.Controls
{
    public partial class StatusPill : UserControl
    {
        public static readonly StyledProperty<ConnectionStatus> StatusProperty =
            AvaloniaProperty.Register<StatusPill, ConnectionStatus>(nameof(Status));

        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<StatusPill, string>(nameof(Text), string.Empty);

        public static readonly StyledProperty<string> DetailProperty =
            AvaloniaProperty.Register<StatusPill, string>(nameof(Detail), string.Empty);

        public ConnectionStatus Status
        {
            get => GetValue(StatusProperty);
            set => SetValue(StatusProperty, value);
        }

        public string Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public string Detail
        {
            get => GetValue(DetailProperty);
            set => SetValue(DetailProperty, value);
        }

        public StatusPill()
        {
            InitializeComponent();
        }
    }
}