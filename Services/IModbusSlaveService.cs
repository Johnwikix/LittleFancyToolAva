using System.Collections.ObjectModel;
using System.IO.Ports;
using LittleFancyToolAva.Models;

namespace LittleFancyToolAva.Services
{
    public interface IModbusSlaveService
    {
        bool IsRunning { get; }
        ObservableCollection<SlaveTableRow> Coils { get; }
        ObservableCollection<SlaveTableRow> HoldingRegisters { get; }
        event Action<string>? LogReceived;
        event Action<string>? StatusChanged;
        Task StartAsync(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, byte slaveId, ushort coilCount, ushort registerCount);
        void Stop();
        Task WriteCoilAsync(int index, bool value);
        Task WriteHoldingRegisterAsync(int index, ushort value);
    }
}
