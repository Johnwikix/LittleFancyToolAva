using Lang.Avalonia;
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

                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    _udpClient = new UdpClient();
                    _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _udpClient.Client.Bind(new IPEndPoint(ip, localPort));
                    ct.ThrowIfCancellationRequested();

                    if (!string.IsNullOrEmpty(multicastAddress) && IPAddress.TryParse(multicastAddress, out var multicastIp))
                    {
                        _udpClient.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 32);
                        _udpClient.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
                        _udpClient.JoinMulticastGroup(multicastIp);
                    }
                }, ct).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(multicastAddress) && IPAddress.TryParse(multicastAddress, out var multicastIp))
                {
                    _logger.LogInformation("UDP joined multicast: {Address}:{Port}", multicastAddress, multicastPort ?? localPort);
                    StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Udp_MulticastJoined", multicastAddress, multicastPort ?? localPort));
                }

                _logger.LogInformation("UDP started: {Address}:{Port}", localAddress, localPort);
                var startedMsg = LocalizationRegistry.Get("ServiceStatus.Udp_Started", localAddress, localPort);
                StatusChanged?.Invoke(startedMsg);
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Connected, startedMsg));
                _ = ReceiveLoopAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                SafeCleanupUdp();
                _logger.LogWarning("UDP start cancelled: {Address}:{Port}", localAddress, localPort);
                var cancelMsg = LocalizationRegistry.Get("ServiceStatus.Udp_StartCancelled");
                StatusChanged?.Invoke(cancelMsg);
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Error, cancelMsg));
                throw;
            }
            catch (Exception ex)
            {
                SafeCleanupUdp();
                _logger.LogError(ex, "UDP start failed: {Address}:{Port}", localAddress, localPort);
                var failMsg = LocalizationRegistry.Get("ServiceStatus.Udp_StartFail", ex.Message);
                StatusChanged?.Invoke(failMsg);
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Error, failMsg, ex));
                throw;
            }
        }

        private void SafeCleanupUdp()
        {
            try
            {
                _udpClient?.Close();
                _udpClient?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UDP safe cleanup error");
            }
            finally
            {
                _udpClient = null;
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
                var stoppedMsg = LocalizationRegistry.Get("ServiceStatus.Udp_Stopped");
                StatusChanged?.Invoke(stoppedMsg);
                if (wasRunning)
                {
                    ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                        ConnectionEventType.Disconnected, stoppedMsg));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UDP stop error");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Udp_StopError", ex.Message));
            }
        }

        public async Task SendAsync(string data, bool isHex, string remoteAddress, int remotePort)
        {
            if (_udpClient == null)
            {
                _logger.LogWarning("SendAsync called but UDP client is null");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Udp_NotReady"));
                return;
            }

            try
            {
                byte[] bytes;
                if (isHex)
                {
                    if (!ToolMethod.TryHexStringToBytes(data, out bytes))
                    {
                        var hexMsg = LocalizationRegistry.Get("ServiceStatus.Udp_HexInvalid");
                        StatusChanged?.Invoke(hexMsg);
                        ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                            ConnectionEventType.Error, hexMsg));
                        return;
                    }
                }
                else
                {
                    bytes = Encoding.UTF8.GetBytes(data);
                }

                var endpoint = new IPEndPoint(
                    IPAddress.TryParse(remoteAddress, out var ip) ? ip : IPAddress.Loopback,
                    remotePort);

                await _udpClient.SendAsync(bytes, bytes.Length, endpoint).ConfigureAwait(false);
                DataSent?.Invoke(bytes);
                _logger.LogDebug("UDP sent {ByteCount} bytes to {Address}:{Port}",
                    bytes.Length, remoteAddress, remotePort);
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Udp_Sent", bytes.Length));
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode == SocketError.NetworkDown ||
                ex.SocketErrorCode == SocketError.NetworkUnreachable ||
                ex.SocketErrorCode == SocketError.HostUnreachable)
            {
                _logger.LogError(ex, "UDP send failed - network unavailable");
                var detailMsg = LocalizationRegistry.Get("ServiceStatus.Udp_NetworkUnreachDetail", ex.Message);
                var shortMsg = LocalizationRegistry.Get("ServiceStatus.Udp_NetworkUnreach");
                StatusChanged?.Invoke(detailMsg);
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Error, shortMsg, ex));
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogWarning(ex, "UDP send on disposed client");
                var closedMsg = LocalizationRegistry.Get("ServiceStatus.Udp_ConnectionClosed", ex.Message);
                var closedShort = LocalizationRegistry.Get("ServiceStatus.Udp_Closed");
                StatusChanged?.Invoke(closedMsg);
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Lost, closedShort, ex));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UDP send error");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Udp_SendFail", ex.Message));
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
                        var stoppedMsg = LocalizationRegistry.Get("ServiceStatus.Udp_NetworkStopped");
                        var netMsg = LocalizationRegistry.Get("ServiceStatus.Udp_NetworkUnreach");
                        StatusChanged?.Invoke(stoppedMsg);
                        ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                            ConnectionEventType.Lost, netMsg));
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
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Udp_ReceiveLoopError", ex.Message));
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Error, LocalizationRegistry.Get("ServiceStatus.Udp_ReceiveLoopError", ex.Message), ex));
            }
        }

        private void AppendToBuffer(byte[] data)
        {
            FlushReceiveBuffer(data);
        }

        private void FlushReceiveBuffer(byte[]? bytes = null)
        {
            byte[] payload;
            lock (_receiveBuffer)
            {
                if (bytes is null)
                {
                    if (_receiveBuffer.Count == 0) return;
                    payload = [.. _receiveBuffer];
                    _receiveBuffer.Clear();
                }
                else
                {
                    payload = bytes;
                }
            }
            BytesReceived?.Invoke(payload);
            _logger.LogDebug("UDP received {ByteCount} bytes", payload.Length);
            StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Udp_Received", payload.Length));
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
