using LittleFancyToolAva.Models;
using LittleFancyToolAva.Utils;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LittleFancyToolAva.Services
{
    public class UdpService : IUdpService, IDisposable
    {
        private UdpClient? _udpClient;
        private CancellationTokenSource? _cts;
        private bool _disposed;
        private readonly List<byte> _receiveBuffer = [];
        private readonly ILogger<UdpService> _logger;

        public bool IsRunning => _udpClient != null;

        public event Action<byte[]>? BytesReceived;
        public event Action<byte[]>? DataSent;
        public event Action<string>? StatusChanged;
        public event EventHandler<ConnectionEventArgs>? ConnectionStateChanged;

        public UdpService(ILogger<UdpService> logger)
        {
            _logger = logger;
            _logger.LogInformation("UdpService created");
        }

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
                    _logger.LogInformation("UDP joined multicast: {Address}:{Port}", multicastAddress, multicastPort ?? localPort);
                    StatusChanged?.Invoke($"已加入组播 {multicastAddress}:{multicastPort ?? localPort}");
                }

                _logger.LogInformation("UDP started: {Address}:{Port}", localAddress, localPort);
                StatusChanged?.Invoke($"UDP 已启动: {localAddress}:{localPort}");
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Connected, $"UDP 已启动: {localAddress}:{localPort}"));
                _ = ReceiveLoopAsync(_cts.Token);
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UDP start failed: {Address}:{Port}", localAddress, localPort);
                StatusChanged?.Invoke($"启动失败: {ex.Message}");
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Error, $"启动失败: {ex.Message}", ex));
                throw;
            }
        }

        public void Stop()
        {
            bool wasRunning = _udpClient != null;
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                _udpClient?.Close();
                _udpClient?.Dispose();
                _udpClient = null;

                lock (_receiveBuffer) { _receiveBuffer.Clear(); }

                _logger.LogInformation("UDP stopped");
                StatusChanged?.Invoke("UDP 已停止");
                if (wasRunning)
                {
                    ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                        ConnectionEventType.Disconnected, "UDP 已停止"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UDP stop error");
                StatusChanged?.Invoke($"停止异常: {ex.Message}");
            }
        }

        public async Task SendAsync(string data, bool isHex, string remoteAddress, int remotePort)
        {
            if (_udpClient == null)
            {
                _logger.LogWarning("SendAsync called but UDP client is null");
                StatusChanged?.Invoke("连接未就绪");
                return;
            }

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

                await _udpClient.SendAsync(bytes, bytes.Length, endpoint);
                DataSent?.Invoke(bytes);
                _logger.LogDebug("UDP sent {ByteCount} bytes to {Address}:{Port}",
                    bytes.Length, remoteAddress, remotePort);
                StatusChanged?.Invoke($"发送: {bytes.Length} 字节");
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode == SocketError.NetworkDown ||
                ex.SocketErrorCode == SocketError.NetworkUnreachable ||
                ex.SocketErrorCode == SocketError.HostUnreachable)
            {
                _logger.LogError(ex, "UDP send failed - network unavailable");
                StatusChanged?.Invoke($"网络不可达: {ex.Message}");
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Error, $"网络不可达", ex));
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogWarning(ex, "UDP send on disposed client");
                StatusChanged?.Invoke($"连接已关闭: {ex.Message}");
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Lost, "UDP 连接已关闭", ex));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UDP send error");
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
                    UdpReceiveResult result;
                    try
                    {
                        result = await _udpClient.ReceiveAsync(ct);
                    }
                    catch (SocketException ex) when (
                        ex.SocketErrorCode == SocketError.NetworkDown ||
                        ex.SocketErrorCode == SocketError.NetworkUnreachable)
                    {
                        _logger.LogError(ex, "UDP receive - network unavailable, stopping");
                        StatusChanged?.Invoke($"网络不可达，UDP 已停止");
                        ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                            ConnectionEventType.Lost, "网络不可达"));
                        Stop();
                        return;
                    }
                    AppendToBuffer(result.Buffer);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UDP receive loop error");
                StatusChanged?.Invoke($"接收循环异常: {ex.Message}");
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Error, $"接收循环异常: {ex.Message}", ex));
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
            _logger.LogDebug("UDP received {ByteCount} bytes", bytes.Length);
            StatusChanged?.Invoke($"接收: {bytes.Length} 字节");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _logger.LogInformation("UdpService disposed");
            GC.SuppressFinalize(this);
        }
    }
}
