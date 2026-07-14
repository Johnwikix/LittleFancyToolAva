using System.Net;
using System.Net.Sockets;
using System.Text;
using LittleFancyToolAva.Utils;

namespace LittleFancyToolAva.Services
{
    public class UdpService : IUdpService, IDisposable
    {
        private UdpClient? _udpClient;
        private CancellationTokenSource? _cts;
        private bool _disposed;
        private readonly List<byte> _receiveBuffer = [];

        public bool IsRunning => _udpClient != null;

        public event Action<byte[]>? BytesReceived;
        public event Action<byte[]>? DataSent;
        public event Action<string>? StatusChanged;

        public async Task StartAsync(string localAddress, int localPort, string? multicastAddress = null, int? multicastPort = null, CancellationToken ct = default)
        {
            try
            {
                Stop();
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var ip = IPAddress.TryParse(localAddress, out var parsed) ? parsed : IPAddress.Any;
                _udpClient = new UdpClient();
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.Client.Bind(new IPEndPoint(ip, localPort));

                if (!string.IsNullOrEmpty(multicastAddress) && IPAddress.TryParse(multicastAddress, out var multicastIp))
                {
                    _udpClient.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 32);
                    _udpClient.JoinMulticastGroup(multicastIp);
                    StatusChanged?.Invoke($"已加入组播 {multicastAddress}:{multicastPort ?? localPort}");
                }

                StatusChanged?.Invoke($"UDP 已启动: {localAddress}:{localPort}");
                _ = ReceiveLoopAsync(_cts.Token);
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"启动失败: {ex.Message}");
                throw;
            }
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                _udpClient?.Close();
                _udpClient?.Dispose();
                _udpClient = null;

                lock (_receiveBuffer) { _receiveBuffer.Clear(); }

                StatusChanged?.Invoke("UDP 已停止");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"停止异常: {ex.Message}");
            }
        }

        public async Task SendAsync(string data, bool isHex, string remoteAddress, int remotePort)
        {
            try
            {
                byte[] bytes;
                if (isHex)
                {
                    string hex = data.Replace(" ", "").Replace("-", "");
                    if (hex.Length % 2 != 0) hex = "0" + hex;
                    bytes = new byte[hex.Length / 2];
                    for (int i = 0; i < hex.Length; i += 2)
                        bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
                }
                else
                {
                    bytes = Encoding.UTF8.GetBytes(data);
                }

                var endpoint = new IPEndPoint(
                    IPAddress.TryParse(remoteAddress, out var ip) ? ip : IPAddress.Loopback,
                    remotePort);

                await _udpClient!.SendAsync(bytes, bytes.Length, endpoint);
                DataSent?.Invoke(bytes);
                string hexStr = ToolMethod.ByteArrayToHexString(bytes);
                StatusChanged?.Invoke($"发送: {bytes.Length} 字节");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"发送失败: {ex.Message}");
            }
        }

        public void SetFrameBreakInterval(int ms)
        {
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _udpClient != null)
                {
                    var result = await _udpClient.ReceiveAsync(ct);
                    AppendToBuffer(result.Buffer);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"接收循环异常: {ex.Message}");
            }
        }

        private void AppendToBuffer(byte[] data)
        {
            lock (_receiveBuffer)
            {
                _receiveBuffer.AddRange(data);
            }
            FlushReceiveBuffer();
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
            BytesReceived?.Invoke(bytes);
            StatusChanged?.Invoke($"接收: {bytes.Length} 字节");
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