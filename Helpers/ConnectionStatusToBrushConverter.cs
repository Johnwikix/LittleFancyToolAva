using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using FancyToolAva.Models;

namespace FancyToolAva.Helpers
{
    public sealed class ConnectionStatusToBrushConverter : IValueConverter
    {
        private static readonly IBrush ConnectedBrush = new SolidColorBrush(Color.FromUInt32(0xFF4CAF50));
        private static readonly IBrush ConnectingBrush = new SolidColorBrush(Color.FromUInt32(0xFFFFC107));
        private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromUInt32(0xFFF44336));
        private static readonly IBrush IdleBrush = new SolidColorBrush(Color.FromUInt32(0xFF9E9E9E));

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not ConnectionStatus status) return IdleBrush;
            return status switch
            {
                ConnectionStatus.Connected => ConnectedBrush,
                ConnectionStatus.Connecting => ConnectingBrush,
                ConnectionStatus.Error => ErrorBrush,
                _ => IdleBrush
            };
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}