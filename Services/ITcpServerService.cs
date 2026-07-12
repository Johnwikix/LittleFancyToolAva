using System.Collections.ObjectModel;

namespace LittleFancyToolAva.Services
{
    public interface ITcpServerService
    {
        bool IsRunning { get; }
        ObservableCollection<string> ConnectedClients { get; }
        event Action<byte[]>? BytesReceived;
        event Action<string>? DataReceived;
        event Action<string>? StatusChanged;
        Task StartServerAsync(string address, int port, CancellationToken ct);
        void StopServer();
        Task ConnectClientAsync(string address, int port, CancellationToken ct);
        void DisconnectClient();
        Task SendAsync(string data, bool isHex, string? targetClient = null);
    }
}
