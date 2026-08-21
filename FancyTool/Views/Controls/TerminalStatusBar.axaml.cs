using Avalonia;
using Avalonia.Controls;

namespace FancyToolAva.Views.Controls
{
    public partial class TerminalStatusBar : UserControl
    {
        public static readonly StyledProperty<int> RxCountProperty =
            AvaloniaProperty.Register<TerminalStatusBar, int>(nameof(RxCount));

        public static readonly StyledProperty<int> TxCountProperty =
            AvaloniaProperty.Register<TerminalStatusBar, int>(nameof(TxCount));

        public static readonly StyledProperty<int> ErrorCountProperty =
            AvaloniaProperty.Register<TerminalStatusBar, int>(nameof(ErrorCount));

        public static readonly StyledProperty<string> ElapsedTextProperty =
            AvaloniaProperty.Register<TerminalStatusBar, string>(nameof(ElapsedText), "00:00:00");

        public static readonly StyledProperty<string> RightDetailProperty =
            AvaloniaProperty.Register<TerminalStatusBar, string>(nameof(RightDetail), string.Empty);

        public int RxCount
        {
            get => GetValue(RxCountProperty);
            set => SetValue(RxCountProperty, value);
        }

        public int TxCount
        {
            get => GetValue(TxCountProperty);
            set => SetValue(TxCountProperty, value);
        }

        public int ErrorCount
        {
            get => GetValue(ErrorCountProperty);
            set => SetValue(ErrorCountProperty, value);
        }

        public string ElapsedText
        {
            get => GetValue(ElapsedTextProperty);
            set => SetValue(ElapsedTextProperty, value);
        }

        public string RightDetail
        {
            get => GetValue(RightDetailProperty);
            set => SetValue(RightDetailProperty, value);
        }

        public TerminalStatusBar()
        {
            InitializeComponent();
        }
    }
}