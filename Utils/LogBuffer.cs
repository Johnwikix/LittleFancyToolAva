using Avalonia.Threading;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using LittleFancyToolAva.Models;

namespace LittleFancyToolAva.Utils
{
    public sealed class LogBuffer : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private readonly ConcurrentQueue<LogEntry> _pending = new();
        private readonly DispatcherTimer _flushTimer;

        public ObservableCollection<LogEntry> Entries { get; } = new();

        public int MaxLines { get; set; } = 5000;

        public int Count => Entries.Count;

        public LogBuffer() : this(16) { }

        public LogBuffer(int flushIntervalMs)
        {
            _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(flushIntervalMs) };
            _flushTimer.Tick += (_, _) => Flush();
            _flushTimer.Start();
        }

        public void Enqueue(LogKind kind, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _pending.Enqueue(new LogEntry(kind, text));
        }

        public void EnqueueLine(LogKind kind, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
                _pending.Enqueue(new LogEntry(kind, line));
        }

        public void Append(LogKind kind, string text) => Enqueue(kind, text);
        public void AppendLine(LogKind kind, string text) => EnqueueLine(kind, text);

        public void Clear()
        {
            while (_pending.TryDequeue(out _)) { }
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

        private void Flush()
        {
            if (_pending.IsEmpty) return;
            if (!Dispatcher.UIThread.CheckAccess()) return;

            List<LogEntry> batch = [];
            while (_pending.TryDequeue(out var e))
            {
                batch.Add(e);
            }
            if (batch.Count == 0) return;

            if (Entries.Count + batch.Count > MaxLines)
            {
                int excess = (Entries.Count + batch.Count) - MaxLines;
                for (int i = 0; i < excess && Entries.Count > 0; i++)
                    Entries.RemoveAt(0);
            }
            foreach (var e in batch)
                Entries.Add(e);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }
    }
}