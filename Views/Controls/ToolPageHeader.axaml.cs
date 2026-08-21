using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace FancyToolAva.Views.Controls
{
    public partial class ToolPageHeader : UserControl
    {
        public static readonly StyledProperty<string> TitleProperty =
            AvaloniaProperty.Register<ToolPageHeader, string>(nameof(Title), string.Empty);

        public static readonly StyledProperty<string> SubtitleProperty =
            AvaloniaProperty.Register<ToolPageHeader, string>(nameof(Subtitle), string.Empty);

        public static readonly StyledProperty<object?> ActionsProperty =
            AvaloniaProperty.Register<ToolPageHeader, object?>(nameof(Actions));

        public string Title
        {
            get => GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public string Subtitle
        {
            get => GetValue(SubtitleProperty);
            set => SetValue(SubtitleProperty, value);
        }

        public object? Actions
        {
            get => GetValue(ActionsProperty);
            set => SetValue(ActionsProperty, value);
        }

        public ToolPageHeader()
        {
            InitializeComponent();
        }
    }
}