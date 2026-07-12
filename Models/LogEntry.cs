using CommunityToolkit.Mvvm.ComponentModel;

namespace LittleFancyToolAva.Models
{
    public partial class LogEntry : ObservableObject
    {
        [ObservableProperty]
        private LogKind _kind;

        [ObservableProperty]
        private string _text = string.Empty;

        [ObservableProperty]
        private DateTime _timestamp;

        [ObservableProperty]
        private string _timestampText = string.Empty;

        [ObservableProperty]
        private string _tag = string.Empty;

        public LogEntry() { }

        public LogEntry(LogKind kind, string text)
        {
            _kind = kind;
            _text = text;
            _timestamp = DateTime.Now;
            _timestampText = _timestamp.ToString("HH:mm:ss.fff");
            _tag = kind switch
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