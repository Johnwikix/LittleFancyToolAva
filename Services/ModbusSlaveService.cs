using LittleFancyToolAva.Models;
using Modbus.Data;
using Modbus.Device;
using System;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

// TODO: 待完善 — 功能可用但暂不启用，后续可完善后重新接入导航
namespace LittleFancyToolAva.Services
{
    public class ModbusSlaveService : IModbusSlaveService, IDisposable
    {
        private SerialPort? _serialPort;
        private ModbusSerialSlave? _slave;
        private bool _disposed;
        private Task? _listenTask;
        private CancellationTokenSource? _listenCts;

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

            PopulateRows(coilCount, registerCount);

            IsRunning = true;
            StatusChanged?.Invoke("Modbus Slave 已启动");

            _listenCts = new CancellationTokenSource();
            _listenTask = Task.Run(() =>
            {
                try { _slave.Listen(); }
                catch (Exception ex) when (_listenCts is { IsCancellationRequested: false })
                { StatusChanged?.Invoke($"监听异常: {ex.Message}"); }
            });
            await Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (!IsRunning) return;
            IsRunning = false;
            _listenCts?.Cancel();
            _slave?.Dispose();
            if (_serialPort?.IsOpen == true)
                _serialPort?.Close();
            _serialPort?.Dispose();
            if (_listenTask is not null)
            {
                try { await _listenTask.ConfigureAwait(false); } catch { }
            }
            _slave = null;
            _serialPort = null;
            _listenTask = null;
            _listenCts?.Dispose();
            _listenCts = null;
            StatusChanged?.Invoke("Modbus Slave 已停止");
        }

        public void Stop()
        {
            _ = StopAsync();
        }

        public Task WriteCoilAsync(int index, bool value)
        {
            var store = _slave?.DataStore;
            if (store is not null && index >= 0 && index < store.CoilDiscretes.Count)
            {
                store.CoilDiscretes[index] = value;
                if (index < Coils.Count)
                {
                    var row = Coils[index];
                    string newValue = value ? "1" : "0";
                    if (row.Value != newValue)
                    {
                        row.Value = newValue;
                    }
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
                    var row = HoldingRegisters[index];
                    string newValue = value.ToString();
                    if (row.Value != newValue)
                    {
                        row.Value = newValue;
                    }
                }
            }
            return Task.CompletedTask;
        }

        private void PopulateRows(ushort coilCount, ushort registerCount)
        {
            Coils.Clear();
            for (int i = 0; i < coilCount; i++)
            {
                Coils.Add(new SlaveTableRow { Address = i.ToString(), Value = "0" });
            }
            HoldingRegisters.Clear();
            for (int i = 0; i < registerCount; i++)
            {
                HoldingRegisters.Add(new SlaveTableRow { Address = i.ToString(), Value = "0" });
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopAsync().GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }
    }
}