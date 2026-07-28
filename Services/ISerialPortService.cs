using LittleFancyToolAva.Models;
using System.IO.Ports;

namespace LittleFancyToolAva.Services
{
    public interface ISerialPortService
    {
        bool IsOpen { get; }
        event Action<byte[]>? BytesReceived;
        event Action<string>? DataReceived;
        event Action<byte[]>? DataSent;
        event Action<string>? StatusChanged;
        event EventHandler<ConnectionEventArgs>? ConnectionStateChanged;
        Task OpenAsync(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, CancellationToken ct = default);
        void Close();
        Task SendAsync(string data, bool isHex, string encoding);
        string[] GetPortNames();
        void SetFrameBreakInterval(int ms);
    }
}
