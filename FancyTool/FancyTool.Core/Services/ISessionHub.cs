namespace FancyToolAva.Services
{
    /// <summary>
    /// 会话编队：页面从它创建与回收会话，进程退出时统一释放尚未回收的会话。
    /// </summary>
    /// <typeparam name="TSession">会话服务类型。</typeparam>
    public interface ISessionHub<TSession> : IDisposable where TSession : IDisposable
    {
        /// <summary>尚未回收的会话数。</summary>
        int Count { get; }

        TSession CreateSession();

        /// <summary>回收会话：停止并释放，之后不再被 Hub 持有。可重复调用。</summary>
        void ReleaseSession(TSession session);
    }
}
