using System.Globalization;
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
            return status switch
            {
                ConnectionStatus.Connected => new SolidColorBrush(Color.FromUInt32(0xFF4CAF50)),
                ConnectionStatus.Connecting => new SolidColorBrush(Color.FromUInt32(0xFFFFC107)),
                ConnectionStatus.Error => new SolidColorBrush(Color.FromUInt32(0xFFF44336)),
                _ => new SolidColorBrush(Color.FromUInt32(0xFF9E9E9E))
            };
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
