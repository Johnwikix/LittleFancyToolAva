using System.Globalization;
using Avalonia.Data.Converters;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.Helpers
{
    public sealed class ConnectionStatusToTextConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not ConnectionStatus status) return LocalizationRegistry.Get("Common.Status_Unknown");
            return status switch
            {
                ConnectionStatus.Idle => LocalizationRegistry.Get("Common.Status_Idle"),
                ConnectionStatus.Connecting => LocalizationRegistry.Get("Common.Status_Connecting"),
                ConnectionStatus.Connected => LocalizationRegistry.Get("Common.Status_Connected"),
                ConnectionStatus.Error => LocalizationRegistry.Get("Common.Status_Error"),
                _ => LocalizationRegistry.Get("Common.Status_Unknown")
            };
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
