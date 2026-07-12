using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LittleFancyToolAva.Utils;

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

        public bool IsRunning => _isServerMode ? _listener != null : IsConnected;

        public bool IsConnected => _client?.Connected ?? false;

        public ObservableCollection<string> ConnectedClients { get; } = [];

        public event Action<byte[]>? BytesReceived;
        public event Action<string>? DataReceived;
        public event Action<string>? StatusChanged;

        public async Task StartServerAsync(string address, int port, CancellationToken ct)
        {
            try
            {
                StopServer();
                _isServerMode = true;

                var ip = IPAddress.TryParse(address, out var parsed) ? parsed : IPAddress.Loopback;
                _listener = new TcpListener(ip, port);
                _listener.Start();

                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                StatusChanged?.Invoke($"服务器已启动: {ip}:{port}");

                _ = AcceptClientsAsync(_cts.Token);
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"启动服务器失败: {ex.Message}");
                throw;
            }
        }

        public void StopServer()
        {
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

                _listener?.Stop();
                _listener = null;

                _client?.Close();
                _client = null;

                StatusChanged?.Invoke("服务器已停止");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"停止异常: {ex.Message}");
            }
        }

        public async Task ConnectClientAsync(string address, int port, CancellationToken ct)
        {
            try
            {
                DisconnectClient();
                _isServerMode = false;

                _client = new TcpClient();
                await _client.ConnectAsync(address, port, ct);

                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                StatusChanged?.Invoke($"已连接: {address}:{port}");

                _ = ReceiveLoopAsync(_client, _cts.Token);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"连接失败: {ex.Message}");
                throw;
            }
        }

        public void DisconnectClient()
        {
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                _client?.Close();
                _client = null;

                StatusChanged?.Invoke("已断开");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"断开异常: {ex.Message}");
            }
        }

        public async Task SendAsync(string data, bool isHex, string? targetClient = null)
        {
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
                    bytes = Encoding.UTF8.GetBytes(data);
                }

                if (_isServerMode)
                {
                    lock (_clients)
                    {
                        var targets = _clients.Where(c => c.Connected).ToList();
                        if (targetClient != null)
                            targets = targets.Where(c => c.Client.RemoteEndPoint?.ToString() == targetClient).ToList();
                        foreach (var c in targets)
                        {
                            try
                            {
                                var stream = c.GetStream();
                                stream.Write(bytes, 0, bytes.Length);
                            }
                            catch { }
                        }
                    }
                    string hexStr = ToolMethod.ByteArrayToHexString(bytes);
                    StatusChanged?.Invoke($"服务器发送: {bytes.Length} 字节 [{hexStr}]");
                }
                else if (_client?.Connected == true)
                {
                    var stream = _client.GetStream();
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                    string hexStr = ToolMethod.ByteArrayToHexString(bytes);
                    StatusChanged?.Invoke($"发送: {bytes.Length} 字节 [{hexStr}]");
                }
                else
                {
                    StatusChanged?.Invoke("未连接");
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"发送失败: {ex.Message}");
            }
        }

        private async Task AcceptClientsAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _listener != null)
                {
                    var client = await _listener.AcceptTcpClientAsync(ct);
                    var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "未知";

                    lock (_clients)
                    {
                        _clients.Add(client);
                    }

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ConnectedClients.Add(endpoint);
                    });

                    StatusChanged?.Invoke($"客户端连接: {endpoint}");

                    _ = ReceiveLoopAsync(client, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"接受连接异常: {ex.Message}");
            }
        }

        private async Task ReceiveLoopAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                var buffer = new byte[4096];
                var stream = client.GetStream();

                while (!ct.IsCancellationRequested && client.Connected)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (read == 0) break;

                    byte[] data = new byte[read];
                    Array.Copy(buffer, data, read);

                    string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "未知";
                    BytesReceived?.Invoke(data);
                    string hex = ToolMethod.ByteArrayToHexString(data);
                    string text = Encoding.UTF8.GetString(data);
                    StatusChanged?.Invoke($"接收 ({endpoint}): {read} 字节 [{hex}]");
                    DataReceived?.Invoke($"[{endpoint}] {text}");
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"接收循环异常: {ex.Message}");
            }
            finally
            {
                string ep = client.Client.RemoteEndPoint?.ToString() ?? "未知";
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ConnectedClients.Remove(ep);
                });
                lock (_clients) { _clients.Remove(client); }
                try { client.Close(); } catch { }
                StatusChanged?.Invoke($"客户端断开: {ep}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopServer();
            GC.SuppressFinalize(this);
        }
    }
}
