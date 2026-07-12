using LittleFancyToolAva.Models;
using Modbus.Data;
using Modbus.Device;
using System;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Threading.Tasks;

namespace LittleFancyToolAva.Services
{
    public class ModbusSlaveService : IModbusSlaveService
    {
        private SerialPort? _serialPort;
        private ModbusSerialSlave? _slave;
        public bool IsRunning { get; private set; }
        public ObservableCollection<SlaveTableRow> Coils { get; } = new();
        public ObservableCollection<SlaveTableRow> HoldingRegisters { get; } = new();

        public event Action<string>? LogReceived;
        public event Action<string>? StatusChanged;

        public async Task StartAsync(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, byte slaveId, ushort coilCount, ushort registerCount)
        {
            _serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
            _serialPort.Open();

            var store = DataStoreFactory.CreateDefaultDataStore(coilCount, 0, registerCount, 0);
            _slave = ModbusSerialSlave.CreateRtu(slaveId, _serialPort);
            _slave.DataStore = store;

            IsRunning = true;
            StatusChanged?.Invoke("Modbus Slave 已启动");

            _ = Task.Run(() => _slave.Listen());
            await Task.CompletedTask;
        }

        public void Stop()
        {
            IsRunning = false;
            _slave?.Dispose();
            _serialPort?.Close();
            _slave = null;
            _serialPort = null;
            StatusChanged?.Invoke("Modbus Slave 已停止");
        }

        public Task WriteCoilAsync(int index, bool value) => Task.CompletedTask;
        public Task WriteHoldingRegisterAsync(int index, ushort value) => Task.CompletedTask;
    }
}
