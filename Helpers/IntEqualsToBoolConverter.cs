using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace LittleFancyToolAva.Helpers
{
    public sealed class IntEqualsToBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is int i && parameter is not null)
            {
                if (int.TryParse(parameter.ToString(), out var p))
                {
                    return i == p;
                }
            }
            return false;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter is not null)
            {
                if (int.TryParse(parameter.ToString(), out var p))
                {
                    return p;
                }
            }
            return null;
        }
    }
}
