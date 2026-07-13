using LittleFancyToolAva.Models;
using Modbus.Data;
using Modbus.Device;
using System;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Threading.Tasks;

namespace LittleFancyToolAva.Services
{
    public class ModbusSlaveService : IModbusSlaveService, IDisposable
    {
        private SerialPort? _serialPort;
        private ModbusSerialSlave? _slave;
        private bool _disposed;
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

            _ = Task.Run(() =>
            {
                try { _slave.Listen(); }
                catch (Exception ex) { StatusChanged?.Invoke($"监听异常: {ex.Message}"); }
            });
            await Task.CompletedTask;
        }

        public void Stop()
        {
            IsRunning = false;
            _slave?.Dispose();
            if (_serialPort?.IsOpen == true)
                _serialPort?.Close();
            _serialPort?.Dispose();
            _slave = null;
            _serialPort = null;
            StatusChanged?.Invoke("Modbus Slave 已停止");
        }

        public Task WriteCoilAsync(int index, bool value)
        {
            var store = _slave?.DataStore;
            if (store is not null && index >= 0 && index < store.CoilDiscretes.Count)
            {
                store.CoilDiscretes[index] = value;
                if (index < Coils.Count)
                {
                    Coils[index] = new SlaveTableRow { Address = index.ToString(), Value = value ? "1" : "0" };
                }
            }
            return Task.CompletedTask;
        }

        public Task WriteHoldingRegisterAsync(int index, ushort value)
        {
            var store = _slave?.DataStore;
            if (store is not null && index >= 0 && index < store.HoldingRegisters.Count)
            {
                store.HoldingRegisters[index] = value;
                if (index < HoldingRegisters.Count)
                {
                    HoldingRegisters[index] = new SlaveTableRow { Address = index.ToString(), Value = value.ToString() };
                }
            }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
