using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using LittleFancyToolAva.Models;

namespace LittleFancyToolAva.Helpers
{
    public sealed class ConnectionStatusToBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not ConnectionStatus status) return Brushes.Gray;
            var key = status switch
            {
                ConnectionStatus.Connected => "SystemAccentColor",
                ConnectionStatus.Connecting => "SystemAccentColor",
                ConnectionStatus.Error => "SystemFillColorCriticalColor",
                _ => "TextFillColorSecondaryColor"
            };

            if (Application.Current?.Resources.TryGetResource(key, null, out var res) == true)
            {
                if (res is Color c) return new SolidColorBrush(c);
                if (res is IBrush b) return b;
            }
            return Brushes.Gray;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}