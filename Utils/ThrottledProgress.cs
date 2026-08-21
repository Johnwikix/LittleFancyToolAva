using Avalonia.Threading;

namespace FancyToolAva.Utils
{
    public sealed class ThrottledProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;
        private readonly DispatcherTimer _timer;
        private T _latest = default!;
        private bool _hasPending;
        private readonly object _lock = new();

        public ThrottledProgress(Action<T> action, TimeSpan interval)
        {
            _action = action;
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        public void Report(T value)
        {
            lock (_lock)
            {
                _latest = value;
                _hasPending = true;
            }
        }

        private void OnTick(object? sender, EventArgs e)
        {
            T value;
            bool hasPending;
            lock (_lock)
            {
                if (!_hasPending) return;
                value = _latest;
                hasPending = true;
                _hasPending = false;
            }
            if (hasPending) _action(value);
        }
    }
}