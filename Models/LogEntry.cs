using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Utils;

namespace LittleFancyToolAva.Models
{
    public partial class LogEntry : ObservableObject
    {
        public LogKind Kind
        {
            get;
            set => SetProperty(ref field, value);
        }

        public string Text
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public DateTime Timestamp
        {
            get;
            set => SetProperty(ref field, value);
        }

        public string TimestampText
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string Tag
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public LogEntry() { }

        public LogEntry(LogKind kind, string text)
        {
            Kind = kind;
            Text = text;
            Timestamp = DateTime.Now;
            TimestampText = Timestamp.ToString("HH:mm:ss.fff");
            Tag = kind switch
            {
                LogKind.Tx => "TX",
                LogKind.Rx => "RX",
                LogKind.Error => "ERR",
                LogKind.System => "SYS",
                _ => "INFO"
            };
        }
    }
}