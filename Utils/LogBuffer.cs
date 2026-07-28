using Avalonia.Collections;
using Avalonia.Threading;
using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using LittleFancyToolAva.Models;

namespace LittleFancyToolAva.Utils
{
    public sealed class LogBuffer : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private readonly ConcurrentQueue<LogEntry> _pending = new();
        private readonly DispatcherTimer _flushTimer;

        public AvaloniaList<LogEntry> Entries { get; } = new();

        public int MaxLines { get; set; } = 100000;

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
            ReadOnlySpan<char> src = text.AsSpan();
            if (src.IsEmpty) return;
            int lineStart = 0;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (c == '\n')
                {
                    int lineEnd = i;
                    if (lineEnd > lineStart && src[lineEnd - 1] == '\r') lineEnd--;
                    if (lineEnd > lineStart)
                    {
                        _pending.Enqueue(new LogEntry(kind, text.Substring(lineStart, lineEnd - lineStart)));
                    }
                    lineStart = i + 1;
                }
            }
            if (lineStart < src.Length)
            {
                _pending.Enqueue(new LogEntry(kind, text.Substring(lineStart)));
            }
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
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(Flush);
                return;
            }

            int batchSize = _pending.Count;
            LogEntry[] batch = ArrayPool<LogEntry>.Shared.Rent(batchSize);
            try
            {
                int count = 0;
                while (_pending.TryDequeue(out var e) && count < batchSize)
                {
                    batch[count++] = e;
                }
                if (count == 0) return;

                int excess = (Entries.Count + count) - MaxLines;
                if (excess > 0)
                {
                    int removeCount = Math.Min(excess, Entries.Count);
                    Entries.RemoveRange(0, removeCount);
                }
                Entries.AddRange(new ArraySegment<LogEntry>(batch, 0, count));
            }
            finally
            {
                for (int i = 0; i < batchSize; i++) batch[i] = null!;
                ArrayPool<LogEntry>.Shared.Return(batch, clearArray: true);
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }
    }
}