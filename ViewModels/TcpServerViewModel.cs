using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.Utils;
using Microsoft.Extensions.Logging;

namespace LittleFancyToolAva.ViewModels
{
    public partial class TcpServerViewModel : ViewModelBase, IDisposable, IViewState, IViewLifecycle
    {
        private readonly ITcpServerService _tcpService;
        private readonly INotificationService _notificationService;
        private readonly IViewStateService _viewStateService;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _pollCts;
        private DispatcherTimer? _elapsedTimer;
        private DateTime? _startedAt;
        private bool _disposed;

        public ObservableCollection<string> Modes { get; } = ["服务器模式", "客户端模式"];
        public ObservableCollection<string> ConnectedClients => _tcpService.ConnectedClients;

        public LogBuffer Log { get; } = new();

        public int ModeIndex
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnModeIndexChanged(value);
                }
            }
        }

        public string Address
        {
            get;
            set => SetProperty(ref field, value);
        } = "127.0.0.1";

        public string Port
        {
            get;
            set => SetProperty(ref field, value);
        } = "8080";

        public string SendText
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string StatusText
        {
            get;
            set => SetProperty(ref field, value);
        } = "就绪";

        public bool IsHexSend
        {
            get;
            set => SetProperty(ref field, value);
        }

        public bool IsHexDisplay
        {
            get;
            set => SetProperty(ref field, value);
        }

        public string SelectedClient
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public bool IsRunning
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnIsRunningChanged(value);
                }
            }
        }

        public bool IsConnected
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnIsConnectedChanged(value);
                }
            }
        }

        public bool IsAnyActive => IsRunning || IsConnected;

        public ConnectionStatus ConnectionStatus
        {
            get;
            set => SetProperty(ref field, value);
        } = ConnectionStatus.Idle;

        public int RxCount
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int TxCount
        {
            get;
            set => SetProperty(ref field, value);
        }

        public string ElapsedText
        {
            get;
            set => SetProperty(ref field, value);
        } = "00:00:00";

        public string StatusDetail
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public int FrameBreakInterval
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnFrameBreakIntervalChanged(value);
                }
            }
        } = 20;

        public bool EnableFrameBreak
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnEnableFrameBreakChanged(value);
                }
            }
        }

        public int PollInterval
        {
            get;
            set => SetProperty(ref field, value);
        } = 1000;

        public bool IsPolling
        {
            get;
            set => SetProperty(ref field, value);
        }

        public bool EnableSendCount
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int SendCount
        {
            get;
            set => SetProperty(ref field, value);
        } = 1;

        string IViewState.ViewName => "tcpServerView";

        public TcpServerViewModel(ITcpServerService tcpService, INotificationService notificationService, IViewStateService viewStateService)
        {
            _tcpService = tcpService;
            _notificationService = notificationService;
            _viewStateService = viewStateService;
            _viewStateService.Register(this);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ((IViewLifecycle)this).OnNavigatedFrom();
            _viewStateService.Unregister(this);
        }

        void IViewLifecycle.OnNavigatedTo()
        {
            _tcpService.BytesReceived += OnBytesReceived;
            _tcpService.DataSent += OnDataSent;
            _tcpService.StatusChanged += OnStatusChanged;
            _tcpService.ConnectionStateChanged += OnConnectionStateChanged;
            if (IsRunning || IsConnected)
            {
                StartElapsedTimer();
            }
        }

        void IViewLifecycle.OnNavigatedFrom()
        {
            _tcpService.BytesReceived -= OnBytesReceived;
            _tcpService.DataSent -= OnDataSent;
            _tcpService.StatusChanged -= OnStatusChanged;
            _tcpService.ConnectionStateChanged -= OnConnectionStateChanged;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
            StopElapsedTimer();
        }

        object IViewState.CaptureState() => new TcpServerViewState
        {
            ModeIndex = ModeIndex,
            Address = Address,
            Port = Port,
            SendText = SendText,
            IsHexSend = IsHexSend,
            IsHexDisplay = IsHexDisplay,
            EnableFrameBreak = EnableFrameBreak,
            FrameBreakInterval = FrameBreakInterval,
            PollInterval = PollInterval,
            EnableSendCount = EnableSendCount,
            SendCount = SendCount
        };

        void IViewState.RestoreState(object state)
        {
            if (state is TcpServerViewState s)
            {
                ModeIndex = s.ModeIndex;
                Address = s.Address;
                Port = s.Port;
                SendText = s.SendText;
                IsHexSend = s.IsHexSend;
                IsHexDisplay = s.IsHexDisplay;
                EnableFrameBreak = s.EnableFrameBreak;
                FrameBreakInterval = s.FrameBreakInterval;
                PollInterval = s.PollInterval;
                EnableSendCount = s.EnableSendCount;
                SendCount = s.SendCount;
            }
        }

        private void OnDataSent(byte[] bytes)
        {
        }

        private void AppendTxLog(string text)
        {
            if (IsHexDisplay)
            {
                byte[] bytes = IsHexSend
                    ? ToolMethod.HexStringToBytes(text)
                    : Encoding.UTF8.GetBytes(text);
                Log.Append(LogKind.Tx, ToolMethod.ByteArrayToHexString(bytes));
            }
            else
            {
                Log.Append(LogKind.Tx, text);
            }
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
            });
        }

        private void OnConnectionStateChanged(object? sender, ConnectionEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (e.Type)
                {
                    case ConnectionEventType.Connected:
                        if (ModeIndex == 1) IsConnected = true;
                        else IsRunning = true;
                        Log.Append(LogKind.System, e.Message);
                        break;
                    case ConnectionEventType.Disconnected:
                        if (ModeIndex == 1) IsConnected = false;
                        else IsRunning = false;
                        IsPolling = false;
                        _pollCts?.Cancel();
                        Log.Append(LogKind.System, e.Message);
                        break;
                    case ConnectionEventType.ClientConnected:
                        Log.Append(LogKind.System, e.Message);
                        break;
                    case ConnectionEventType.ClientDisconnected:
                        Log.Append(LogKind.System, e.Message);
                        break;
                    case ConnectionEventType.Lost:
                        if (ModeIndex == 1) IsConnected = false;
                        else IsRunning = false;
                        IsPolling = false;
                        _pollCts?.Cancel();
                        Log.Append(LogKind.Error, e.Message);
                        break;
                    case ConnectionEventType.PingTimeout:
                        Log.Append(LogKind.Warn, e.Message);
                        break;
                    case ConnectionEventType.Error:
                        ConnectionStatus = ConnectionStatus.Error;
                        Log.Append(LogKind.Error, e.Message);
                        break;
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

        private void OnIsRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(IsAnyActive));
            UpdateConnectionState();
        }
        private void OnIsConnectedChanged(bool value)
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
                AppendTxLog(SendText);
                TxCount++;
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
            if (IsPolling) return;

            var localCts = new CancellationTokenSource();
            _pollCts = localCts;
            IsPolling = true;
            int sentCount = 0;
            try
            {
                while (!localCts.Token.IsCancellationRequested)
                {
                    if (!IsRunning && !IsConnected) break;
                    await _tcpService.SendAsync(SendText, IsHexSend, null);
                    AppendTxLog(SendText);
                    TxCount++;
                    sentCount++;
                    if (EnableSendCount && sentCount >= SendCount) break;
                    await Task.Delay(PollInterval, localCts.Token);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Log.Append(LogKind.Error, $"定时发送错误: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_pollCts, localCts))
                {
                    IsPolling = false;
                }
            }
        }

        [RelayCommand]
        private void StopPolling()
        {
            _pollCts?.Cancel();
        }

        private void OnFrameBreakIntervalChanged(int value)
        {
            _tcpService.SetFrameBreakInterval(value);
        }

        private void OnEnableFrameBreakChanged(bool value)
        {
            _tcpService.EnableFrameBreak = value;
        }

        private void OnModeIndexChanged(int value)
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