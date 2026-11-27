using Avalonia.Threading;

namespace FancyToolAva.ViewModels
{
    /// <summary>
    /// 终端类调试页共用的 UI 节拍：16ms 把各会话的日志与计数刷到界面，1s 刷新计时与聚合状态。
    /// 以单例提供，随容器释放而停止；页面不再各自创建 DispatcherTimer。
    /// </summary>
    public sealed class TerminalTicker : IDisposable
    {
        private readonly DispatcherTimer _fastTimer;
        private readonly DispatcherTimer _slowTimer;

        public TerminalTicker()
        {
            _fastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _fastTimer.Tick += (_, _) => FastTick?.Invoke();
            _fastTimer.Start();

            _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _slowTimer.Tick += (_, _) => SlowTick?.Invoke();
            _slowTimer.Start();
        }

        /// <summary>日志与计数上屏。</summary>
        public event Action? FastTick;

        /// <summary>计时与聚合状态刷新。</summary>
        public event Action? SlowTick;

        public void Dispose()
        {
            _fastTimer.Stop();
            _slowTimer.Stop();
            FastTick = null;
            SlowTick = null;
        }
    }
}
