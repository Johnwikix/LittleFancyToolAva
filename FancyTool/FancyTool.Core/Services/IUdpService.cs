using FancyToolAva.Models;

namespace FancyToolAva.Services
{
    public interface IUdpService
    {
        bool IsRunning { get; }
        event Action<byte[]>? BytesReceived;
        event Action<byte[]>? DataSent;
        event Action<string>? StatusChanged;
        event EventHandler<ConnectionEventArgs>? ConnectionStateChanged;
        Task StartAsync(string localAddress, int localPort, string? multicastAddress = null, int? multicastPort = null, CancellationToken ct = default);
        void Stop();
        Task SendAsync(string data, bool isHex, string remoteAddress, int remotePort);
        void SetFrameBreakInterval(int ms);
    }
}