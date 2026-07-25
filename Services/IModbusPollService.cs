using System.IO.Ports;

// TODO: 待完善 — 功能可用但暂不启用，后续可完善后重新接入导航
namespace LittleFancyToolAva.Services
{
    public interface IModbusPollService
    {
        bool IsConnected { get; }
        int TxCount { get; }
        int ErrorCount { get; }
        event Action<string>? LogReceived;
        event Action<string>? StatusChanged;
        event Action<ushort, ushort, byte>? ValueRefreshed;
        event Action? StatsUpdated;
        Task ConnectAsync(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits);
        void Disconnect();
        Task StopAsync();
        Task StartPollingAsync(byte slaveId, byte functionCode, ushort startAddress, ushort quantity, int scanTimeMs, CancellationToken ct);
        Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value);
        Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value);
    }
}
