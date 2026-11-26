using FancyToolAva.Models;

namespace FancyToolAva.Services
{
    /// <summary>
    /// 串口会话编队：负责创建/回收会话、维护可用端口目录，并在退出时统一释放端口句柄。
    /// </summary>
    public interface ISerialPortHub : IDisposable
    {
        IReadOnlyList<ISerialPortService> Sessions { get; }

        string[] GetAvailablePorts();

        ISerialPortService CreateSession();

        void ReleaseSession(ISerialPortService session);

        Task DisconnectAllAsync();

        void RaiseStatus(string message);
    }
}