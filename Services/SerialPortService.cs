using System.Buffers;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using Lang.Avalonia;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Utils;
using Microsoft.Extensions.Logging;

namespace LittleFancyToolAva.Services
{
    public class SerialPortService : ISerialPortService, IDisposable
    {
        private const int ReceiveReadBufferSize = 4096;

        private SerialPort? _serialPort;
        private readonly List<byte> _receiveBuffer = [];
        private Timer? _frameTimer;
        private int _frameBreakInterval = 20;
        private bool _disposed;
        private readonly ILogger<SerialPortService> _logger;
        private bool _isClosing;
        private int _timerGen;
        private readonly object _timerLock = new();

        public bool IsOpen => _serialPort?.IsOpen ?? false;

        public event Action<byte[]>? BytesReceived;
        public event Action<string>? DataReceived;
        public event Action<byte[]>? DataSent;
        public event Action<string>? StatusChanged;
        public event EventHandler<ConnectionEventArgs>? ConnectionStateChanged;

        public SerialPortService(ILogger<SerialPortService> logger)
        {
            _logger = logger;
            _logger.LogInformation("SerialPortService created");
        }

        public async Task OpenAsync(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, CancellationToken ct = default)
        {
            try
            {
                Close();

                _isClosing = false;

                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    _serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
                    {
                        ReadTimeout = 500,
                        WriteTimeout = 500
                    };
                    _serialPort.DataReceived += OnSerialDataReceived;
                    _serialPort.ErrorReceived += OnSerialErrorReceived;
                    _serialPort.PinChanged += OnSerialPinChanged;
                    _serialPort.Open();
                    ct.ThrowIfCancellationRequested();
                }, ct).ConfigureAwait(false);

                _logger.LogInformation("SerialPort opened: {PortName} ({BaudRate},{Parity},{DataBits},{StopBits})",
                    portName, baudRate, parity, dataBits, stopBits);

                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_Connected", portName, baudRate, parity, dataBits, stopBits));
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Connected, LocalizationRegistry.Get("Serial.Status_ConnectedShort", portName)));
            }
            catch (OperationCanceledException)
            {
                SafeCleanupSerialPort();
                _logger.LogWarning("SerialPort open cancelled: {PortName}", portName);
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_Cancel"));
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Error, LocalizationRegistry.Get("ServiceStatus.Serial_Timeout")));
                throw;
            }
            catch (Exception ex)
            {
                SafeCleanupSerialPort();
                _logger.LogError(ex, "SerialPort open failed: {PortName}", portName);
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_ConnectFail", ex.Message));
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Error, LocalizationRegistry.Get("ServiceStatus.Serial_ConnectFail", ex.Message), ex));
                throw;
            }
        }

        private void SafeCleanupSerialPort()
        {
            try
            {
                if (_serialPort != null)
                {
                    _serialPort.DataReceived -= OnSerialDataReceived;
                    _serialPort.ErrorReceived -= OnSerialErrorReceived;
                    _serialPort.PinChanged -= OnSerialPinChanged;
                    if (_serialPort.IsOpen) _serialPort.Close();
                    _serialPort.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SerialPort safe cleanup error");
            }
            finally
            {
                _serialPort = null;
            }
        }

        public void Close()
        {
            _isClosing = true;
            bool wasOpen = _serialPort != null;
            try
            {
                lock (_timerLock) { _frameTimer?.Dispose(); _frameTimer = null; }
                if (_serialPort != null)
                {
                    _serialPort.DataReceived -= OnSerialDataReceived;
                    _serialPort.ErrorReceived -= OnSerialErrorReceived;
                    _serialPort.PinChanged -= OnSerialPinChanged;
                    if (_serialPort.IsOpen)
                    {
                        _serialPort.Close();
                        _logger.LogInformation("SerialPort closed by user");
                    }
                    _serialPort.Dispose();
                    _serialPort = null;
                }
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_Disconnected"));
                if (wasOpen)
                {
                    ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                        ConnectionEventType.Disconnected, LocalizationRegistry.Get("ServiceStatus.Serial_Closed")));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SerialPort close error");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_CloseError", ex.Message));
            }
        }

        public async Task SendAsync(string data, bool isHex, string encoding)
        {
            if (_serialPort is not { IsOpen: true })
            {
                _logger.LogWarning("SendAsync called but port not open");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_NotOpen"));
                return;
            }

            try
            {
                byte[] bytes;
                if (isHex)
                {
                    if (!ToolMethod.TryHexStringToBytes(data, out bytes) || bytes.Length == 0)
                    {
                        StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_HexInvalid"));
                        ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                            ConnectionEventType.Error, LocalizationRegistry.Get("ServiceStatus.Serial_HexInvalid")));
                        return;
                    }
                }
                else
                {
                    var mode = encoding switch
                    {
                        "UTF8" => ToolMethod.EncodingMode.UTF8,
                        "ASCII" => ToolMethod.EncodingMode.ASCII,
                        "GB2312" or "GB18030" => ToolMethod.EncodingMode.GB2312,
                        _ => ToolMethod.EncodingMode.Auto
                    };
                    bytes = ToolMethod.GetEncodedData(data, mode);
                }

                await _serialPort.BaseStream.WriteAsync(bytes).ConfigureAwait(false);
                await _serialPort.BaseStream.FlushAsync().ConfigureAwait(false);

                DataSent?.Invoke(bytes);
                _logger.LogDebug("SerialPort sent {ByteCount} bytes", bytes.Length);
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_BytesSent", bytes.Length));
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "SerialPort send IOException - connection lost");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_SendFail", ex.Message));
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Lost, LocalizationRegistry.Get("ServiceStatus.Serial_SendLost", ex.Message), ex));
                RaiseLostIfOpen();
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "SerialPort send InvalidOperation - port closed");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_SendFail", ex.Message));
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Lost, LocalizationRegistry.Get("ServiceStatus.Serial_PortClosed", ex.Message), ex));
                RaiseLostIfOpen();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SerialPort send error");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_SendFail", ex.Message));
            }
        }

        public string[] GetPortNames()
        {
            return SerialPort.GetPortNames();
        }

        public void SetFrameBreakInterval(int ms)
        {
            _frameBreakInterval = Math.Max(10, ms);
        }

        private void OnSerialDataReceived(object? sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (_serialPort is not { IsOpen: true }) return;

                int bytesToRead = _serialPort.BytesToRead;
                if (bytesToRead <= 0) return;

                byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(bytesToRead, ReceiveReadBufferSize));
                try
                {
                    int read = _serialPort.Read(rented, 0, bytesToRead);
                    if (read > 0)
                    {
                        lock (_receiveBuffer)
                        {
                            int currentCount = _receiveBuffer.Count;
                            CollectionsMarshal.SetCount(_receiveBuffer, currentCount + read);
                            var span = CollectionsMarshal.AsSpan(_receiveBuffer);
                            rented.AsSpan(0, read).CopyTo(span.Slice(currentCount, read));
                        }
                        ResetFrameTimer();
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented, clearArray: false);
                }
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "SerialPort data received on closed port");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SerialPort receive error");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_ReceiveError", ex.Message));
            }
        }

        private void OnSerialErrorReceived(object? sender, SerialErrorReceivedEventArgs e)
        {
            _logger.LogWarning("SerialPort error received: {EventType}", e.EventType);
            StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_PortError", e.EventType));

            if (e.EventType is SerialError.Frame or SerialError.RXOver or SerialError.TXFull or SerialError.RXParity)
            {
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Error, LocalizationRegistry.Get("ServiceStatus.Serial_PortError", e.EventType)));
            }
        }

        private void OnSerialPinChanged(object? sender, SerialPinChangedEventArgs e)
        {
            _logger.LogDebug("SerialPort pin changed: {EventType}", e.EventType);

            if (e.EventType == SerialPinChange.CDChanged && _serialPort is { IsOpen: true })
            {
                bool cdState = _serialPort.CDHolding;
                _logger.LogInformation("SerialPort CD (Carrier Detect) changed: {CDState}", cdState);
                StatusChanged?.Invoke(LocalizationRegistry.Get(
                    "ServiceStatus.Serial_CDSignal",
                    cdState ? LocalizationRegistry.Get("ServiceStatus.Serial_CDDetect") : LocalizationRegistry.Get("ServiceStatus.Serial_CDLost")));

                if (!cdState)
                {
                    ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                        ConnectionEventType.LineDisconnect, LocalizationRegistry.Get("ServiceStatus.Serial_CDLineDisconnect")));
                }
            }
        }

        private void RaiseLostIfOpen()
        {
            if (IsOpen && !_isClosing)
            {
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Lost, LocalizationRegistry.Get("ServiceStatus.Serial_Disconnected")));
            }
        }

        private void ResetFrameTimer()
        {
            int gen;
            lock (_timerLock)
            {
                gen = ++_timerGen;
                _frameTimer?.Dispose();
                _frameTimer = new Timer(OnTimerTick, gen, _frameBreakInterval, Timeout.Infinite);
            }
        }

        private void OnTimerTick(object? state)
        {
            try
            {
                int gen = (int)state!;
                lock (_timerLock)
                {
                    if (gen != _timerGen) return;
                    _frameTimer?.Dispose();
                    _frameTimer = null;
                }
                FlushReceiveBuffer();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SerialPort frame timer error");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_FrameTimerError", ex.Message));
            }
        }

        private void FlushReceiveBuffer()
        {
            byte[] bytes;
            lock (_receiveBuffer)
            {
                if (_receiveBuffer.Count == 0) return;
                bytes = _receiveBuffer.ToArray();
                _receiveBuffer.Clear();
            }

            _logger.LogDebug("SerialPort received {ByteCount} bytes", bytes.Length);
            StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Serial_BytesReceived", bytes.Length));
            BytesReceived?.Invoke(bytes);
            DataReceived?.Invoke(DecodeBytes(bytes, "Auto"));
        }

        private static string DecodeBytes(byte[] bytes, string encoding)
        {
            var mode = encoding switch
            {
                "UTF8" => ToolMethod.EncodingMode.UTF8,
                "ASCII" => ToolMethod.EncodingMode.ASCII,
                "GB2312" or "GB18030" => ToolMethod.EncodingMode.GB2312,
                _ => ToolMethod.EncodingMode.Auto
            };
            try
            {
                return ToolMethod.GetEncoding(mode).GetString(bytes);
            }
            catch
            {
                return ToolMethod.ByteArrayToHexString(bytes);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
            _logger.LogInformation("SerialPortService disposed");
            GC.SuppressFinalize(this);
        }
    }
}
