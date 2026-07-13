using LittleFancyToolAva.Utils;
using Modbus.Device;
using Modbus.Data;
using System;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LittleFancyToolAva.Services
{
    public class ModbusPollService : IModbusPollService, IDisposable
    {
        private SerialPort? _serialPort;
        private ModbusSerialMaster? _master;
        private bool _disposed;

        public bool IsConnected { get; private set; }
        public int TxCount { get; private set; }
        public int ErrorCount { get; private set; }
        public event Action<string>? LogReceived;
        public event Action<string>? StatusChanged;
        public event Action<ushort, ushort, byte>? ValueRefreshed;
        public event Action? StatsUpdated;

        public Task ConnectAsync(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits)
        {
            return Task.Run(() =>
            {
                _serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
                _serialPort.Open();
                _master = ModbusSerialMaster.CreateRtu(_serialPort);
                IsConnected = true;
                StatusChanged?.Invoke("已连接到 " + portName);
            });
        }

        public void Disconnect()
        {
            IsConnected = false;
            _master?.Dispose();
            if (_serialPort?.IsOpen == true)
                _serialPort?.Close();
            _serialPort?.Dispose();
            _master = null;
            _serialPort = null;
            StatusChanged?.Invoke("已断开");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            GC.SuppressFinalize(this);
        }

        public async Task StartPollingAsync(byte slaveId, byte functionCode, ushort startAddress, ushort quantity, int scanTimeMs, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                try
                {
                    if (functionCode == 1 || functionCode == 2)
                    {
                        var coils = functionCode == 1
                            ? await _master!.ReadCoilsAsync(slaveId, startAddress, quantity)
                            : await _master!.ReadInputsAsync(slaveId, startAddress, quantity);
                        TxCount++;
                        LogReceived?.Invoke($"FC{functionCode}: {string.Join(",", coils)}");
                    }
                    else
                    {
                        var registers = functionCode == 3
                            ? await _master!.ReadHoldingRegistersAsync(slaveId, startAddress, quantity)
                            : await _master!.ReadInputRegistersAsync(slaveId, startAddress, quantity);
                        TxCount++;
                        LogReceived?.Invoke($"FC{functionCode}: {string.Join(",", registers)}");
                    }
                }
                catch (Exception ex)
                {
                    ErrorCount++;
                    LogReceived?.Invoke($"错误: {ex.Message}");
                }

                await Task.Delay(scanTimeMs, ct);
            }
        }

        public async Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value)
        {
            if (_master == null) return;
            await _master.WriteSingleCoilAsync(slaveId, address, value);
            TxCount++;
        }

        public async Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value)
        {
            if (_master == null) return;
            await _master.WriteSingleRegisterAsync(slaveId, address, value);
            TxCount++;
        }
    }
}
