using System.Globalization;
using Avalonia.Data.Converters;
using LittleFancyToolAva.Models;

namespace LittleFancyToolAva.Helpers
{
    public sealed class ConnectionStatusToTextConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not ConnectionStatus status) return "未知";
            return status switch
            {
                ConnectionStatus.Idle => "未连接",
                ConnectionStatus.Connecting => "连接中",
                ConnectionStatus.Connected => "已连接",
                ConnectionStatus.Error => "错误",
                _ => "未知"
            };
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}