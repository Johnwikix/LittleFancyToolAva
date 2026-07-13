using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.Utils;

namespace LittleFancyToolAva.ViewModels
{
    public partial class TcpServerViewModel : ViewModelBase, IDisposable
    {
        private readonly ITcpServerService _tcpService;
        private readonly INotificationService _notificationService;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _pollCts;
        private DispatcherTimer? _elapsedTimer;
        private DateTime? _startedAt;
        private bool _disposed;

        public ObservableCollection<string> Modes { get; } = ["服务器模式", "客户端模式"];
        public ObservableCollection<string> ConnectedClients => _tcpService.ConnectedClients;

        public LogBuffer Log { get; } = new();

        [ObservableProperty]
        private int _modeIndex;

        [ObservableProperty]
        private string _address = "127.0.0.1";

        [ObservableProperty]
        private string _port = "8080";

        [ObservableProperty]
        private string _sendText = string.Empty;

        [ObservableProperty]
        private string _statusText = "就绪";

        [ObservableProperty]
        private bool _isHexSend;

        [ObservableProperty]
        private bool _isHexDisplay;

        [ObservableProperty]
        private string _selectedClient = string.Empty;

        [ObservableProperty]
        private bool _isRunning;

        [ObservableProperty]
        private bool _isConnected;

        public bool IsAnyActive => IsRunning || IsConnected;

        [ObservableProperty]
        private ConnectionStatus _connectionStatus = ConnectionStatus.Idle;

        [ObservableProperty]
        private int _rxCount;

        [ObservableProperty]
        private int _txCount;

        [ObservableProperty]
        private string _elapsedText = "00:00:00";

        [ObservableProperty]
        private string _statusDetail = string.Empty;

        [ObservableProperty]
        private int _frameBreakInterval = 20;

        [ObservableProperty]
        private bool _enableFrameBreak;

        [ObservableProperty]
        private int _pollInterval = 1000;

        [ObservableProperty]
        private bool _isPolling;

        public TcpServerViewModel(ITcpServerService tcpService, INotificationService notificationService)
        {
            _tcpService = tcpService;
            _notificationService = notificationService;
            _tcpService.BytesReceived += OnBytesReceived;
            _tcpService.DataSent += OnDataSent;
            _tcpService.StatusChanged += OnStatusChanged;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _tcpService.BytesReceived -= OnBytesReceived;
            _tcpService.DataSent -= OnDataSent;
            _tcpService.StatusChanged -= OnStatusChanged;
            _cts?.Cancel();
            _cts?.Dispose();
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            StopElapsedTimer();
        }

        private void OnDataSent(byte[] bytes)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsHexDisplay)
                {
                    Log.Append(LogKind.Tx, ToolMethod.ByteArrayToHexString(bytes));
                }
                else
                {
                    Log.Append(LogKind.Tx, Encoding.UTF8.GetString(bytes));
                }
                TxCount++;
            });
        }

        private void OnBytesReceived(byte[] bytes)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsHexDisplay)
                {
                    Log.Append(LogKind.Rx, ToolMethod.ByteArrayToHexString(bytes));
                }
                else
                {
                    Log.AppendLine(LogKind.Rx, Encoding.UTF8.GetString(bytes));
                }
                RxCount++;
            });
        }

        private void OnStatusChanged(string status)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = status;
                if (status == "连接已断开")
                {
                    IsConnected = false;
                }
            });
        }

        private void StartElapsedTimer()
        {
            StopElapsedTimer();
            _startedAt = DateTime.Now;
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += (_, _) =>
            {
                if (_startedAt is { } start)
                {
                    ElapsedText = (DateTime.Now - start).ToString(@"hh\:mm\:ss");
                }
            };
            _elapsedTimer.Start();
        }

        private void StopElapsedTimer()
        {
            _elapsedTimer?.Stop();
            _elapsedTimer = null;
            _startedAt = null;
            ElapsedText = "00:00:00";
        }

        partial void OnIsRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(IsAnyActive));
            UpdateConnectionState();
        }
        partial void OnIsConnectedChanged(bool value)
        {
            OnPropertyChanged(nameof(IsAnyActive));
            UpdateConnectionState();
        }

        private void UpdateConnectionState()
        {
            bool active = IsRunning || IsConnected;
            if (active)
            {
                ConnectionStatus = ConnectionStatus.Connected;
                StatusDetail = ModeIndex == 0
                    ? $"服务器 {Address}:{Port}"
                    : $"客户端 {Address}:{Port}";
                StartElapsedTimer();
            }
            else
            {
                ConnectionStatus = ConnectionStatus.Idle;
                StatusDetail = string.Empty;
                StopElapsedTimer();
            }
        }

        [RelayCommand]
        private async Task Start()
        {
            if (!int.TryParse(Port, out int portNum) || portNum < 1 || portNum > 65535)
            {
                _notificationService.ShowWarn("端口号须在 1-65535 之间。");
                return;
            }

            try
            {
                _cts = new CancellationTokenSource();
                ConnectionStatus = ConnectionStatus.Connecting;

                if (ModeIndex == 0)
                {
                    await _tcpService.StartServerAsync(Address, portNum, _cts.Token);
                    IsRunning = _tcpService.IsRunning;
                }
                else
                {
                    await _tcpService.ConnectClientAsync(Address, portNum, _cts.Token);
                    IsConnected = true;
                }

                if (IsRunning || IsConnected)
                {
                    Log.Append(LogKind.System, $"{(ModeIndex == 0 ? "服务器" : "客户端")}已启动 {Address}:{portNum}");
                }
            }
            catch (Exception ex)
            {
                ConnectionStatus = ConnectionStatus.Error;
                Log.Append(LogKind.Error, $"启动失败: {ex.Message}");
                _notificationService.ShowError($"启动失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private void Stop()
        {
            _pollCts?.Cancel();
            _cts?.Cancel();
            IsPolling = false;
            if (ModeIndex == 0)
            {
                _tcpService.StopServer();
                IsRunning = false;
            }
            else
            {
                _tcpService.DisconnectClient();
                IsConnected = false;
            }
            Log.Append(LogKind.System, "已停止");
        }

        [RelayCommand]
        private void DisconnectClient()
        {
            if (string.IsNullOrEmpty(SelectedClient))
            {
                _notificationService.ShowWarn("请先选择一个客户端。");
                return;
            }
            _tcpService.DisconnectClient(SelectedClient);
            Log.Append(LogKind.System, $"已断开客户端: {SelectedClient}");
            SelectedClient = string.Empty;
        }

        [RelayCommand]
        private async Task Send()
        {
            if (string.IsNullOrEmpty(SendText)) return;

            if (ModeIndex == 0 && !IsRunning)
            {
                _notificationService.ShowWarn("请先启动服务器。");
                return;
            }
            if (ModeIndex != 0 && !IsConnected)
            {
                _notificationService.ShowWarn("请先连接服务器。");
                return;
            }

            string? target = ModeIndex == 0 ? SelectedClient : null;
            if (ModeIndex == 0 && string.IsNullOrEmpty(target) && ConnectedClients.Count > 0)
            {
                target = null;
            }

            try
            {
                await _tcpService.SendAsync(SendText, IsHexSend, target);
            }
            catch (Exception ex)
            {
                Log.Append(LogKind.Error, $"发送失败: {ex.Message}");
                _notificationService.ShowError($"发送失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private void Clear()
        {
            Log.Clear();
            RxCount = 0;
            TxCount = 0;
        }

        [RelayCommand]
        private async Task StartPolling()
        {
            if (!IsRunning && !IsConnected)
            {
                _notificationService.ShowWarn("请先启动服务器或连接客户端。");
                return;
            }

            IsPolling = true;
            _pollCts = new CancellationTokenSource();
            try
            {
                await _tcpService.SendWithIntervalAsync(SendText, IsHexSend, PollInterval, _pollCts.Token);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Log.Append(LogKind.Error, $"定时发送错误: {ex.Message}");
            }
            finally
            {
                IsPolling = false;
            }
        }

        [RelayCommand]
        private void StopPolling()
        {
            _pollCts?.Cancel();
            IsPolling = false;
        }

        partial void OnFrameBreakIntervalChanged(int value)
        {
            _tcpService.SetFrameBreakInterval(value);
        }

        partial void OnEnableFrameBreakChanged(bool value)
        {
            _tcpService.EnableFrameBreak = value;
        }

        partial void OnModeIndexChanged(int value)
        {
            if (value == 0)
            {
                _tcpService.DisconnectClient();
                IsConnected = false;
                StatusText = "就绪 (服务器模式)";
            }
            else
            {
                _tcpService.StopServer();
                IsRunning = false;
                StatusText = "就绪 (客户端模式)";
            }
        }
    }
}