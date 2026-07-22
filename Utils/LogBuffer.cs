using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using LittleFancyToolAva.Models;

namespace LittleFancyToolAva.Utils
{
    public sealed class LogBuffer : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<LogEntry> Entries { get; } = new();

        public int MaxLines { get; set; } = 5000;

        public int Count => Entries.Count;

        public void Append(LogKind kind, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (Dispatcher.UIThread.CheckAccess())
            {
                AddEntry(kind, text);
            }
            else
            {
                Dispatcher.UIThread.Post(() => AddEntry(kind, text));
            }
        }

        public void AppendLine(LogKind kind, string text)
        {
            foreach (var line in SplitLines(text))
            {
                Append(kind, line);
            }
        }

        public void Clear()
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Entries.Clear();
            }
            else
            {
                Dispatcher.UIThread.Post(() => Entries.Clear());
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }

        private void AddEntry(LogKind kind, string text)
        {
            Entries.Add(new LogEntry(kind, text));
            Trim();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }

        private void Trim()
        {
            int excess = Entries.Count - MaxLines;
            if (excess > 0)
            {
                for (int i = 0; i < excess; i++)
                    Entries.RemoveAt(0);
            }
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            return text.Replace("\r\n", "\n").Split('\n');
        }
    }
}
