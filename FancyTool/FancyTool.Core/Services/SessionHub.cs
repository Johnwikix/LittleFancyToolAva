using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services
{
    /// <summary>
    /// 通用会话编队。实例由工厂委托创建，不经容器解析服务接口：
    /// 否则每创建一个会话，根容器都会把它登记为待释放对象并一直持有到进程退出，关闭会话后对象无法回收。
    /// </summary>
    /// <typeparam name="TSession">会话服务类型。</typeparam>
    public sealed class SessionHub<TSession> : ISessionHub<TSession> where TSession : IDisposable
    {
        private readonly Func<TSession> _factory;
        private readonly ILogger _logger;
        private readonly List<TSession> _sessions = [];
        private readonly object _gate = new();
        private bool _disposed;

        public SessionHub(Func<TSession> factory, ILogger<SessionHub<TSession>> logger)
        {
            _factory = factory;
            _logger = logger;
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _sessions.Count;
                }
            }
        }

        public TSession CreateSession()
        {
            var session = _factory();
            lock (_gate)
            {
                if (_disposed)
                {
                    session.Dispose();
                    ObjectDisposedException.ThrowIf(_disposed, this);
                }
                _sessions.Add(session);
            }
            return session;
        }

        public void ReleaseSession(TSession session)
        {
            lock (_gate)
            {
                _sessions.Remove(session);
            }
            DisposeSession(session);
        }

        public void Dispose()
        {
            List<TSession> snapshot;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                snapshot = [.. _sessions];
                _sessions.Clear();
            }
            foreach (var session in snapshot)
            {
                DisposeSession(session);
            }
            _logger.LogInformation("{SessionType} hub disposed ({Count} sessions)", typeof(TSession).Name, snapshot.Count);
        }

        private void DisposeSession(TSession session)
        {
            try
            {
                session.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Releasing {SessionType} session failed", typeof(TSession).Name);
            }
        }
    }
}
