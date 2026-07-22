using LittleFancyToolAva.Models;
using System.Collections.ObjectModel;

namespace LittleFancyToolAva.Services
{
    public interface ITcpServerService
    {
        bool IsRunning { get; }
        ObservableCollection<string> ConnectedClients { get; }
        event Action<byte[]>? BytesReceived;
        event Action<byte[]>? DataSent;
        event Action<string>? DataReceived;
        event Action<string>? StatusChanged;
        event EventHandler<ConnectionEventArgs>? ConnectionStateChanged;
        Task StartServerAsync(string address, int port, CancellationToken ct);
        void StopServer();
        Task ConnectClientAsync(string address, int port, CancellationToken ct);
        void DisconnectClient();
        void DisconnectClient(string endpoint);
        Task SendAsync(string data, bool isHex, string? targetClient = null);
        bool EnableFrameBreak { get; set; }
        void SetFrameBreakInterval(int ms);
    }
}
