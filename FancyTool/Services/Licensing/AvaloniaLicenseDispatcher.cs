using Avalonia.Threading;

namespace FancyToolAva.Services;

internal sealed class AvaloniaLicenseDispatcher : ILicenseUiDispatcher
{
    public static AvaloniaLicenseDispatcher Instance { get; } = new();

    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public async Task InvokeAsync(Func<Task> action)
    {
        if (CheckAccess())
            await action();
        else
            await Dispatcher.UIThread.InvokeAsync(action);
    }

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        Dispatcher.UIThread.VerifyAccess();
        return new TimerLease(interval, tick);
    }

    private sealed class TimerLease : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly Action _tick;
        private bool _disposed;

        public TimerLease(TimeSpan interval, Action tick)
        {
            _tick = tick;
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs args)
        {
            if (!_disposed) _tick();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
    }
}
