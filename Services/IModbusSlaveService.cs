using System.Collections.ObjectModel;
using System.IO.Ports;
using LittleFancyToolAva.Models;

// TODO: 待完善 — 功能可用但暂不启用，后续可完善后重新接入导航
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
        Task StopAsync();
        Task WriteCoilAsync(int index, bool value);
        Task WriteHoldingRegisterAsync(int index, ushort value);
    }
}
