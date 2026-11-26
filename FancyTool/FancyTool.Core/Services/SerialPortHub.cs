using FancyToolAva.Models;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services
{
    /// <summary>
    /// 单例 Hub：所有串口会话都由它创建与释放，页面关闭会话时端口句柄立即归还。
    /// </summary>
    public sealed class SerialPortHub : ISerialPortHub
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<SerialPortHub> _logger;
        private readonly List<ISerialPortService> _sessions = [];
        private readonly object _gate = new();
        private bool _disposed;

        public SerialPortHub(ILoggerFactory loggerFactory, ILogger<SerialPortHub> logger)
        {
            _loggerFactory = loggerFactory;
            _logger = logger;
        }

        public IReadOnlyList<ISerialPortService> Sessions
        {
            get
            {
                lock (_gate)
                {
                    return _sessions.ToArray();
                }
            }
        }

        public string[] GetAvailablePorts() => SerialPortCatalog.GetPortNames();

        public ISerialPortService CreateSession()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = new SerialPortSession(_loggerFactory.CreateLogger<SerialPortSession>());
            lock (_gate)
            {
                _sessions.Add(session);
            }
            return session;
        }

        public void ReleaseSession(ISerialPortService session)
        {
            lock (_gate)
            {
                _sessions.Remove(session);
            }
            try
            {
                session.Close();
                if (session is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Release serial session failed");
            }
        }

        public async Task DisconnectAllAsync()
        {
            ISerialPortService[] snapshot;
            lock (_gate)
            {
                snapshot = _sessions.ToArray();
            }
            foreach (var session in snapshot)
            {
                try
                {
                    session.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Disconnect serial session failed");
                }
            }
            await Task.CompletedTask;
        }

        public void RaiseStatus(string message)
        {
            foreach (var session in Sessions)
            {
                // 会话自身会通过 StatusChanged 广播，这里仅保持接口可扩展。
                _ = session;
            }
            _logger.LogDebug("Serial hub status: {Message}", message);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ISerialPortService[] snapshot;
            lock (_gate)
            {
                snapshot = _sessions.ToArray();
                _sessions.Clear();
            }
            foreach (var session in snapshot)
            {
                try
                {
                    session.Close();
                    if (session is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Dispose serial session failed");
                }
            }
            _logger.LogInformation("SerialPortHub disposed ({Count} sessions)", snapshot.Length);
        }
    }
}