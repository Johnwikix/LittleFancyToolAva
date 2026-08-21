using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using FancyToolAva.Models;

namespace FancyToolAva.Helpers
{
    public sealed class LogKindToBrushConverter : IValueConverter
    {
        private const string DefaultBrushKey = "TextFillColorSecondaryBrush";

        private IBrush _txBrush = Brushes.Gray;
        private IBrush _rxBrush = Brushes.Gray;
        private IBrush _errorBrush = Brushes.Gray;
        private IBrush _systemBrush = Brushes.Gray;
        private IBrush _defaultBrush = Brushes.Gray;
        private bool _initialized;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            EnsureInitialized();
            if (value is not LogKind kind) return _defaultBrush;
            return kind switch
            {
                LogKind.Tx => _txBrush,
                LogKind.Rx => _rxBrush,
                LogKind.Error => _errorBrush,
                LogKind.System => _systemBrush,
                _ => _defaultBrush
            };
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        private void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;
            Refresh();
            if (Application.Current is { } app)
            {
                app.ActualThemeVariantChanged += (_, _) => Refresh();
            }
        }

        private void Refresh()
        {
            _txBrush = ResolveBrush("SystemAccentColor");
            _rxBrush = ResolveBrush("SystemFillColorSuccessBrush");
            _errorBrush = ResolveBrush("SystemFillColorCriticalBrush");
            _systemBrush = ResolveBrush("TextFillColorTertiaryBrush");
            _defaultBrush = ResolveBrush(DefaultBrushKey);
        }

        private static IBrush ResolveBrush(string key)
        {
            if (Application.Current?.Resources.TryGetResource(key, null, out var res) == true)
            {
                if (res is Color c) return new SolidColorBrush(c);
                if (res is IBrush b) return b;
            }
            return Brushes.Gray;
        }
    }
}