using System.IO.Ports;
using System.Text;
using LittleFancyToolAva.Utils;

namespace LittleFancyToolAva.Services
{
    public class SerialPortService : ISerialPortService, IDisposable
    {
        private SerialPort? _serialPort;
        private readonly List<byte> _receiveBuffer = [];
        private Timer? _frameTimer;
        private int _frameBreakInterval = 20;
        private bool _disposed;

        public bool IsOpen => _serialPort?.IsOpen ?? false;

        public event Action<byte[]>? BytesReceived;
        public event Action<string>? DataReceived;
        public event Action<string>? StatusChanged;

        public Task OpenAsync(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits)
        {
            try
            {
                Close();

                _serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };

                _serialPort.DataReceived += OnSerialDataReceived;
                _serialPort.ErrorReceived += OnSerialErrorReceived;
                _serialPort.Open();

                StatusChanged?.Invoke($"已连接 {portName} ({baudRate},{parity},{dataBits},{stopBits})");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"连接失败: {ex.Message}");
                throw;
            }
        }

        public void Close()
        {
            try
            {
                StopFrameTimer();
                if (_serialPort != null)
                {
                    _serialPort.DataReceived -= OnSerialDataReceived;
                    _serialPort.ErrorReceived -= OnSerialErrorReceived;
                    if (_serialPort.IsOpen)
                    {
                        _serialPort.Close();
                    }
                    _serialPort.Dispose();
                    _serialPort = null;
                }
                StatusChanged?.Invoke("已断开");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"关闭异常: {ex.Message}");
            }
        }

        public async Task SendAsync(string data, bool isHex, string encoding)
        {
            if (_serialPort is not { IsOpen: true })
            {
                StatusChanged?.Invoke("串口未打开");
                return;
            }

            try
            {
                byte[] bytes;
                if (isHex)
                {
                    string hex = data.Replace(" ", "").Replace("-", "");
                    if (hex.Length % 2 != 0)
                        hex = "0" + hex;
                    bytes = new byte[hex.Length / 2];
                    for (int i = 0; i < hex.Length; i += 2)
                        bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
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

                await _serialPort.BaseStream.WriteAsync(bytes);
                await _serialPort.BaseStream.FlushAsync();

                string hexStr = ToolMethod.ByteArrayToHexString(bytes);
                StatusChanged?.Invoke($"发送: {bytes.Length} 字节 [{hexStr}]");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"发送失败: {ex.Message}");
            }
        }

        public string[] GetPortNames()
        {
            return SerialPort.GetPortNames();
        }

        public async Task SendWithIntervalAsync(string data, bool isHex, string encoding, int intervalMs, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await SendAsync(data, isHex, encoding);
                    await Task.Delay(intervalMs, ct);
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"定时发送异常: {ex.Message}");
            }
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
                byte[] buffer = new byte[bytesToRead];
                int read = _serialPort.Read(buffer, 0, bytesToRead);
                if (read > 0)
                {
                    lock (_receiveBuffer)
                    {
                        _receiveBuffer.AddRange(buffer.Take(read));
                    }
                    ResetFrameTimer();
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"接收异常: {ex.Message}");
            }
        }

        private void OnSerialErrorReceived(object? sender, SerialErrorReceivedEventArgs e)
        {
            StatusChanged?.Invoke($"串口错误: {e.EventType}");
        }

        private void ResetFrameTimer()
        {
            StopFrameTimer();
            _frameTimer = new Timer(_ =>
            {
                FlushReceiveBuffer();
                StopFrameTimer();
            }, null, _frameBreakInterval, Timeout.Infinite);
        }

        private void StopFrameTimer()
        {
            _frameTimer?.Dispose();
            _frameTimer = null;
        }

        private void FlushReceiveBuffer()
        {
            byte[] bytes;
            lock (_receiveBuffer)
            {
                if (_receiveBuffer.Count == 0) return;
                bytes = [.. _receiveBuffer];
                _receiveBuffer.Clear();
            }

            string hex = ToolMethod.ByteArrayToHexString(bytes);
            StatusChanged?.Invoke($"接收: {bytes.Length} 字节 [{hex}]");
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

        private static string DetectEncoding(byte[] bytes)
        {
            if (bytes.Length == 0) return string.Empty;

            bool isAscii = bytes.All(b => b < 128);
            if (isAscii)
                return Encoding.ASCII.GetString(bytes);

            try
            {
                string utf8 = Encoding.UTF8.GetString(bytes);
                byte[] reEncoded = Encoding.UTF8.GetBytes(utf8);
                if (reEncoded.SequenceEqual(bytes))
                    return utf8;
            }
            catch
            {
            }

            try
            {
                return Encoding.GetEncoding("GB18030").GetString(bytes);
            }
            catch
            {
                return Encoding.ASCII.GetString(bytes);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
            GC.SuppressFinalize(this);
        }
    }
}
