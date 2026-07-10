using System.IO.Ports;

using System.IO.Ports;

namespace LittleFancyToolAva.Services
{
    public interface ISerialPortService
    {
        bool IsOpen { get; }
        event Action<string>? DataReceived;
        event Action<string>? StatusChanged;
        Task OpenAsync(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits);
        void Close();
        Task SendAsync(string data, bool isHex, string encoding);
        string[] GetPortNames();
        Task SendWithIntervalAsync(string data, bool isHex, string encoding, int intervalMs, CancellationToken ct);
    }
}
