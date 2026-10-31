using Avalonia.Threading;

namespace FancyToolAva.Services;

internal sealed class AvaloniaLicenseDispatcher : ILicenseDispatcher
{
    public async Task InvokeAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess()) await action();
        else await Dispatcher.UIThread.InvokeAsync(action);
    }

    public IDisposable StartTimer(TimeSpan interval, Action tick) => new TimerLease(interval, tick);

    private sealed class TimerLease : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly Action _tick;

        public TimerLease(TimeSpan interval, Action tick)
        {
            Dispatcher.UIThread.VerifyAccess();
            _tick = tick;
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs args) => _tick();

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
    }
}
