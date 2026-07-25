using LittleFancyToolAva.Utils;
using Modbus.Device;
using Modbus.Data;
using System;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// TODO: 待完善 — 功能可用但暂不启用，后续可完善后重新接入导航
namespace LittleFancyToolAva.Services
{
    public class ModbusPollService : IModbusPollService, IDisposable
    {
        private SerialPort? _serialPort;
        private ModbusSerialMaster? _master;
        private bool _disposed;
        private CancellationTokenSource? _pollCts;

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
            _ = StopAsync();
        }

        public async Task StopAsync()
        {
            if (!IsConnected && _master is null && _serialPort is null) return;
            IsConnected = false;
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
            try { _master?.Dispose(); } catch { }
            try
            {
                if (_serialPort?.IsOpen == true) _serialPort.Close();
                _serialPort?.Dispose();
            }
            catch { }
            _master = null;
            _serialPort = null;
            StatusChanged?.Invoke("已断开");
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopAsync().GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }

        public async Task StartPollingAsync(byte slaveId, byte functionCode, ushort startAddress, ushort quantity, int scanTimeMs, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && IsConnected && _master is not null)
            {
                try
                {
                    if (functionCode == 1 || functionCode == 2)
                    {
                        var coils = functionCode == 1
                            ? await _master.ReadCoilsAsync(slaveId, startAddress, quantity)
                            : await _master.ReadInputsAsync(slaveId, startAddress, quantity);
                        TxCount++;
                        LogReceived?.Invoke($"FC{functionCode}: {string.Join(",", coils)}");
                    }
                    else
                    {
                        var registers = functionCode == 3
                            ? await _master.ReadHoldingRegistersAsync(slaveId, startAddress, quantity)
                            : await _master.ReadInputRegistersAsync(slaveId, startAddress, quantity);
                        TxCount++;
                        LogReceived?.Invoke($"FC{functionCode}: {string.Join(",", registers)}");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    ErrorCount++;
                    LogReceived?.Invoke($"错误: {ex.Message}");
                }

                try { await Task.Delay(scanTimeMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        public async Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value)
        {
            if (_master is null) return;
            await _master.WriteSingleCoilAsync(slaveId, address, value);
            TxCount++;
        }

        public async Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value)
        {
            if (_master is null) return;
            await _master.WriteSingleRegisterAsync(slaveId, address, value);
            TxCount++;
        }
    }
}