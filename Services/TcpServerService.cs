using Lang.Avalonia;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Utils;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace LittleFancyToolAva.Services
{
    public class TcpServerService : ITcpServerService, IDisposable
    {
        private TcpListener? _listener;
        private TcpClient? _client;
        private readonly List<TcpClient> _clients = [];
        private CancellationTokenSource? _cts;
        private bool _isServerMode;
        private bool _disposed;
        private int _frameBreakInterval = 20;
        private bool _enableFrameBreak;
        private readonly ConcurrentDictionary<string, ClientBuffer> _clientBuffers = [];
        private readonly ILogger<TcpServerService> _logger;
        private bool _isStopping;

        public bool IsRunning => _isServerMode ? _listener != null : IsConnected;
        public bool IsConnected => _client?.Connected ?? false;

        public ObservableCollection<string> ConnectedClients { get; } = [];

        public event Action<byte[]>? BytesReceived;
        public event Action<byte[]>? DataSent;
        public event Action<string>? DataReceived;
        public event Action<string>? StatusChanged;
        public event EventHandler<ConnectionEventArgs>? ConnectionStateChanged;

        public TcpServerService(ILogger<TcpServerService> logger)
        {
            _logger = logger;
            _logger.LogInformation("TcpServerService created");
        }

        public bool EnableFrameBreak
        {
            get => _enableFrameBreak;
            set
            {
                if (_enableFrameBreak == value) return;
                _enableFrameBreak = value;
                if (!value) FlushAllBuffers();
            }
        }

        public void SetFrameBreakInterval(int ms)
        {
            _frameBreakInterval = Math.Max(10, ms);
        }

        public async Task StartServerAsync(string address, int port, CancellationToken ct)
        {
            try
            {
                StopServer();
                _isServerMode = true;
                _isStopping = false;

                var ip = IPAddress.TryParse(address, out var parsed) ? parsed : IPAddress.Loopback;

                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    _listener = new TcpListener(ip, port);
                    _listener.Start();
                    ct.ThrowIfCancellationRequested();
                }, ct).ConfigureAwait(false);

                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                _logger.LogInformation("TCP server started: {Address}:{Port}", ip, port);
                var startedMsg = LocalizationRegistry.Get("ServiceStatus.Tcp_Started", ip, port);
                StatusChanged?.Invoke(startedMsg);
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Connected, startedMsg, endpoint: $"{ip}:{port}"));

                _ = AcceptClientsAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                SafeCleanupListener();
                _logger.LogWarning("TCP server start cancelled: {Address}:{Port}", address, port);
                var cancelMsg = LocalizationRegistry.Get("ServiceStatus.Tcp_StartCancelled");
                StatusChanged?.Invoke(cancelMsg);
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Error, cancelMsg));
                throw;
            }
            catch (Exception ex)
            {
                SafeCleanupListener();
                _logger.LogError(ex, "TCP server start failed: {Address}:{Port}", address, port);
                var failMsg = LocalizationRegistry.Get("ServiceStatus.Tcp_StartFail", ex.Message);
                StatusChanged?.Invoke(failMsg);
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Error, failMsg, ex));
                throw;
            }
        }

        private void SafeCleanupListener()
        {
            try
            {
                _listener?.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TCP listener safe cleanup error");
            }
            finally
            {
                _listener = null;
            }
        }

        public void StopServer()
        {
            _isStopping = true;
            bool wasRunning = _listener != null;
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                lock (_clients)
                {
                    foreach (var c in _clients)
                    {
                        try { c.Close(); } catch { }
                    }
                    _clients.Clear();
                }
                ConnectedClients.Clear();
                CleanupAllClientBuffers();

                _listener?.Stop();
                _listener = null;

                _client?.Close();
                _client = null;

                _logger.LogInformation("TCP server stopped by user");
                var stoppedMsg = LocalizationRegistry.Get("ServiceStatus.Tcp_Stopped");
                StatusChanged?.Invoke(stoppedMsg);
                if (wasRunning)
                {
                    ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                        ConnectionEventType.Disconnected, stoppedMsg));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TCP server stop error");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Tcp_StopError", ex.Message));
            }
        }

        public async Task ConnectClientAsync(string address, int port, CancellationToken ct)
        {
            try
            {
                DisconnectClient();
                _isServerMode = false;
                _isStopping = false;

                _client = new TcpClient();
                _client.NoDelay = true;
                await _client.ConnectAsync(address, port, ct);

                EnableTcpKeepAlive(_client);

                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                _logger.LogInformation("TCP client connected: {Address}:{Port}", address, port);
                var connMsg = LocalizationRegistry.Get("ServiceStatus.Tcp_Connected", address, port);
                StatusChanged?.Invoke(connMsg);
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Connected, connMsg, endpoint: $"{address}:{port}"));

                _ = ReceiveLoopAsync(_client, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TCP client connect failed: {Address}:{Port}", address, port);
                var failMsg = LocalizationRegistry.Get("ServiceStatus.Tcp_ConnectFail", ex.Message);
                StatusChanged?.Invoke(failMsg);
                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                    ConnectionEventType.Error, failMsg, ex));
                throw;
            }
        }

        public void DisconnectClient()
        {
            _isStopping = true;
            bool wasConnected = _client != null;
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                _client?.Close();
                _client = null;

                _logger.LogInformation("TCP client disconnected by user");
                var discMsg = LocalizationRegistry.Get("ServiceStatus.Tcp_Disconnected");
                StatusChanged?.Invoke(discMsg);
                if (wasConnected)
                {
                    ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                        ConnectionEventType.Disconnected, discMsg));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TCP client disconnect error");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Tcp_DisconnectError", ex.Message));
            }
        }

        public void DisconnectClient(string endpoint)
        {
            try
            {
                TcpClient? target = null;
                lock (_clients)
                {
                    target = _clients.FirstOrDefault(c =>
                        c.Client?.RemoteEndPoint?.ToString() == endpoint);
                    if (target != null)
                    {
                        _clients.Remove(target);
                    }
                }

                if (target != null)
                {
                    CleanupClientBuffer(endpoint);
                    try { target.Close(); } catch { }
                    Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ConnectedClients.Remove(endpoint);
                    });
                    _logger.LogInformation("TCP server disconnected client: {Endpoint}", endpoint);
                    var msg = LocalizationRegistry.Get("ServiceStatus.Tcp_ClientDisconnected", endpoint);
                    StatusChanged?.Invoke(msg);
                    ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                        ConnectionEventType.Disconnected, msg, endpoint: endpoint));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TCP disconnect client error: {Endpoint}", endpoint);
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Tcp_ClientDisconnectError", ex.Message));
            }
        }

        public async Task SendAsync(string data, bool isHex, string? targetClient = null)
        {
            try
            {
                byte[] bytes;
                if (isHex)
                {
                    if (!ToolMethod.TryHexStringToBytes(data, out bytes))
                    {
                        var hexMsg = LocalizationRegistry.Get("ServiceStatus.Tcp_HexInvalid");
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

                if (_isServerMode)
                {
                    List<TcpClient> targets;
                    lock (_clients)
                    {
                        targets = _clients.Where(c => c.Connected).ToList();
                        if (targetClient != null)
                            targets = targets.Where(c => c.Client?.RemoteEndPoint?.ToString() == targetClient).ToList();
                    }
                    if (targets.Count == 0)
                    {
                        StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Tcp_NoClients"));
                        return;
                    }
                    foreach (var c in targets)
                    {
                        try
                        {
                            var stream = c.GetStream();
                            await stream.WriteAsync(bytes).ConfigureAwait(false);
                        }
                        catch (IOException ex)
                        {
                            _logger.LogWarning(ex, "TCP send to client failed, client may be disconnected");
                            var ep = c.Client?.RemoteEndPoint?.ToString();
                            if (ep != null)
                                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                                    ConnectionEventType.Lost, LocalizationRegistry.Get("ServiceStatus.Tcp_ClientLost", ep), ex, ep));
                        }
                        catch (SocketException ex)
                        {
                            _logger.LogWarning(ex, "TCP send socket error to client");
                            var ep = c.Client?.RemoteEndPoint?.ToString();
                            if (ep != null)
                                ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                                    ConnectionEventType.PingTimeout, LocalizationRegistry.Get("ServiceStatus.Tcp_ClientTimeout", ep), ex, ep));
                        }
                    }
                    DataSent?.Invoke(bytes);
                    StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Tcp_ServerSent", bytes.Length));
                }
                else if (_client?.Connected == true)
                {
                    try
                    {
                        var stream = _client.GetStream();
                        await stream.WriteAsync(bytes).ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                    }
                    catch (IOException ex)
                    {
                        _logger.LogError(ex, "TCP client send IOException - connection lost");
                        RaiseLost(LocalizationRegistry.Get("ServiceStatus.Tcp_SendLost"));
                        return;
                    }
                    catch (SocketException ex)
                    {
                        _logger.LogError(ex, "TCP client send SocketException");
                        RaiseLost(LocalizationRegistry.Get("ServiceStatus.Tcp_ConnectionError", ex.Message), ex);
                        return;
                    }
                    DataSent?.Invoke(bytes);
                    StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Tcp_Sent", bytes.Length));
                }
                else
                {
                    StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Tcp_NotConnected"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TCP send error");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Tcp_SendFail", ex.Message));
            }
        }

        private void RaiseLost(string message, Exception? ex = null)
        {
            StatusChanged?.Invoke(message);
            ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                ConnectionEventType.Lost, message, ex));
        }

        private void EnableTcpKeepAlive(TcpClient client)
        {
            try
            {
                var socket = client.Client;
                if (socket == null) return;

                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    byte[] keepAliveValues = new byte[12];
                    BitConverter.GetBytes(1).CopyTo(keepAliveValues, 0);
                    BitConverter.GetBytes(10_000).CopyTo(keepAliveValues, 4);
                    BitConverter.GetBytes(3_000).CopyTo(keepAliveValues, 8);
                    socket.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to configure TCP KeepAlive");
            }
        }

        private async Task AcceptClientsAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _listener != null)
                {
                    var client = await _listener.AcceptTcpClientAsync(ct);
                    client.NoDelay = true;
                    EnableTcpKeepAlive(client);

                    var endpoint = client.Client?.RemoteEndPoint?.ToString() ?? LocalizationRegistry.Get("ServiceStatus.Tcp_Unknown");

                    lock (_clients)
                    {
                        _clients.Add(client);
                    }

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ConnectedClients.Add(endpoint);
                    });

                    _logger.LogInformation("TCP client connected: {Endpoint}", endpoint);
                    var msg = LocalizationRegistry.Get("ServiceStatus.Tcp_ClientConnected", endpoint);
                    StatusChanged?.Invoke(msg);
                    ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                        ConnectionEventType.ClientConnected, msg, endpoint: endpoint));

                    _ = ReceiveLoopAsync(client, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TCP accept clients error");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Tcp_AcceptError", ex.Message));
            }
        }

        private async Task ReceiveLoopAsync(TcpClient client, CancellationToken ct)
        {
            var endpoint = client.Client?.RemoteEndPoint?.ToString() ?? LocalizationRegistry.Get("ServiceStatus.Tcp_Unknown");
            try
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
                var stream = client.GetStream();

                while (!ct.IsCancellationRequested && client.Connected)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer.AsMemory(0, 4096), ct);
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "TCP receive IOException from {Endpoint}", endpoint);
                        break;
                    }
                    catch (SocketException ex)
                    {
                        _logger.LogWarning(ex, "TCP receive SocketException from {Endpoint}", endpoint);
                        break;
                    }

                    if (read == 0)
                    {
                        _logger.LogInformation("TCP peer closed connection: {Endpoint}", endpoint);
                        break;
                    }

                    AppendToClientBuffer(endpoint, buffer, read);
                }
                ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TCP receive loop error from {Endpoint}", endpoint);
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Tcp_ReceiveLoopError", ex.Message));
            }
            finally
            {
                FlushClientBuffer(endpoint);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ConnectedClients.Remove(endpoint);
                });
                lock (_clients) { _clients.Remove(client); }
                CleanupClientBuffer(endpoint);
                try { client.Close(); } catch { }

                if (!_isServerMode)
                {
                    _client = null;
                    if (!_isStopping)
                    {
                        _logger.LogWarning("TCP client connection lost: {Endpoint}", endpoint);
                        var lostMsg = LocalizationRegistry.Get("ServiceStatus.Tcp_Disconnected");
                        StatusChanged?.Invoke(lostMsg);
                        ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                            ConnectionEventType.Lost, lostMsg, endpoint: endpoint));
                    }
                }
                else
                {
                    _logger.LogInformation("TCP client disconnected: {Endpoint}", endpoint);
                    var discMsg = LocalizationRegistry.Get("ServiceStatus.Tcp_ClientDisconnectedEvt", endpoint);
                    StatusChanged?.Invoke(discMsg);
                    ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(
                        ConnectionEventType.ClientDisconnected, discMsg, endpoint: endpoint));
                }
            }
        }

        private class ClientBuffer
        {
            public List<byte> Buffer { get; } = [];
            public Timer? Timer { get; set; }
            public int TimerGen { get; set; }
        }

        private ClientBuffer GetOrCreateClientBuffer(string endpoint)
        {
            return _clientBuffers.GetOrAdd(endpoint, _ => new ClientBuffer());
        }

        private void AppendToClientBuffer(string endpoint, byte[] rented, int length)
        {
            bool shouldFlush = false;
            var cb = GetOrCreateClientBuffer(endpoint);
            lock (cb.Buffer)
            {
                int currentCount = cb.Buffer.Count;
                CollectionsMarshal.SetCount(cb.Buffer, currentCount + length);
                var span = CollectionsMarshal.AsSpan(cb.Buffer);
                rented.AsSpan(0, length).CopyTo(span.Slice(currentCount, length));
                if (_enableFrameBreak)
                {
                    int gen = ++cb.TimerGen;
                    cb.Timer?.Dispose();
                    var capturedGen = gen;
                    cb.Timer = new Timer(OnClientTimerTick, (endpoint, capturedGen), _frameBreakInterval, Timeout.Infinite);
                }
                else
                {
                    shouldFlush = true;
                }
            }
            if (shouldFlush) FlushClientBuffer(endpoint);
        }

        private void FlushAllBuffers()
        {
            foreach (var endpoint in _clientBuffers.Keys.ToArray())
            {
                if (_clientBuffers.TryGetValue(endpoint, out var cb))
                {
                    lock (cb.Buffer)
                    {
                        cb.Timer?.Dispose();
                        cb.Timer = null;
                    }
                }
                FlushClientBuffer(endpoint);
            }
        }

        private void OnClientTimerTick(object? state)
        {
            try
            {
                var (endpoint, gen) = ((string, int))state!;
                if (!_clientBuffers.TryGetValue(endpoint, out var cb)) return;
                lock (cb.Buffer)
                {
                    if (gen != cb.TimerGen) return;
                    cb.Timer?.Dispose();
                    cb.Timer = null;
                }
                FlushClientBuffer(endpoint);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TCP client frame timer error: {Endpoint}",
                    state is (string ep, _) ? ep : "?");
                StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Tcp_ClientFrameTimerError", ex.Message));
            }
        }

        private void FlushClientBuffer(string endpoint)
        {
            if (!_clientBuffers.TryGetValue(endpoint, out var cb)) return;
            byte[] bytes;
            lock (cb.Buffer)
            {
                if (cb.Buffer.Count == 0) return;
                bytes = [.. cb.Buffer];
                cb.Buffer.Clear();
            }

            BytesReceived?.Invoke(bytes);
            string text = Encoding.UTF8.GetString(bytes);
            _logger.LogDebug("TCP received {ByteCount} bytes from {Endpoint}", bytes.Length, endpoint);
            StatusChanged?.Invoke(LocalizationRegistry.Get("ServiceStatus.Tcp_ClientReceived", endpoint, bytes.Length));
            DataReceived?.Invoke($"[{endpoint}] {text}");
        }

        private void CleanupClientBuffer(string endpoint)
        {
            if (_clientBuffers.TryRemove(endpoint, out var cb))
            {
                cb.Timer?.Dispose();
                lock (cb.Buffer) { cb.Buffer.Clear(); }
            }
        }

        private void CleanupAllClientBuffers()
        {
            foreach (var kvp in _clientBuffers)
            {
                kvp.Value.Timer?.Dispose();
            }
            _clientBuffers.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopServer();
            _logger.LogInformation("TcpServerService disposed");
            GC.SuppressFinalize(this);
        }
    }
}
