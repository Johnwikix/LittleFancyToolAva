using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.Utils;

namespace LittleFancyToolAva.ViewModels
{
    public partial class UdpViewModel : ViewModelBase, IDisposable, IViewState, IViewLifecycle
    {
        private readonly IUdpService _udpService;
        private readonly INotificationService _notificationService;
        private readonly IViewStateService _viewStateService;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _pollCts;
        private DispatcherTimer? _elapsedTimer;
        private DateTime? _startedAt;
        private bool _disposed;

        public LogBuffer Log { get; } = new();

        [ObservableProperty]
        private int _modeIndex;

        [ObservableProperty]
        private string _localAddress = "0.0.0.0";

        [ObservableProperty]
        private string _localPort = "8080";

        [ObservableProperty]
        private string _multicastAddress = "239.0.0.1";

        [ObservableProperty]
        private string _multicastPort = "8080";

        [ObservableProperty]
        private string _remoteAddress = "127.0.0.1";

        [ObservableProperty]
        private string _remotePort = "9090";

        [ObservableProperty]
        private string _sendText = string.Empty;

        [ObservableProperty]
        private string _statusText = "就绪";

        [ObservableProperty]
        private bool _isHexSend;

        [ObservableProperty]
        private bool _isHexDisplay;

        [ObservableProperty]
        private bool _isRunning;

        public bool IsAnyActive => IsRunning;

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
        private int _pollInterval = 1000;

        [ObservableProperty]
        private bool _isPolling;

        string IViewState.ViewName => "udpView";

        public UdpViewModel(IUdpService udpService, INotificationService notificationService, IViewStateService viewStateService)
        {
            _udpService = udpService;
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
            _udpService.BytesReceived += OnBytesReceived;
            _udpService.DataSent += OnDataSent;
            _udpService.StatusChanged += OnStatusChanged;
            if (IsRunning)
            {
                StartElapsedTimer();
            }
        }

        void IViewLifecycle.OnNavigatedFrom()
        {
            _udpService.BytesReceived -= OnBytesReceived;
            _udpService.DataSent -= OnDataSent;
            _udpService.StatusChanged -= OnStatusChanged;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
            StopElapsedTimer();
        }

        object IViewState.CaptureState() => new UdpViewState
        {
            ModeIndex = ModeIndex,
            LocalAddress = LocalAddress,
            LocalPort = LocalPort,
            MulticastAddress = MulticastAddress,
            MulticastPort = MulticastPort,
            RemoteAddress = RemoteAddress,
            RemotePort = RemotePort,
            SendText = SendText,
            IsHexSend = IsHexSend,
            IsHexDisplay = IsHexDisplay,
            FrameBreakInterval = FrameBreakInterval,
            PollInterval = PollInterval
        };

        void IViewState.RestoreState(object state)
        {
            if (state is UdpViewState s)
            {
                ModeIndex = s.ModeIndex;
                LocalAddress = s.LocalAddress;
                LocalPort = s.LocalPort;
                MulticastAddress = s.MulticastAddress;
                MulticastPort = s.MulticastPort;
                RemoteAddress = s.RemoteAddress;
                RemotePort = s.RemotePort;
                SendText = s.SendText;
                IsHexSend = s.IsHexSend;
                IsHexDisplay = s.IsHexDisplay;
                FrameBreakInterval = s.FrameBreakInterval;
                PollInterval = s.PollInterval;
            }
        }

        private void OnDataSent(byte[] bytes)
        {
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
            Dispatcher.UIThread.Post(() => StatusText = status);
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
            if (value)
            {
                ConnectionStatus = ConnectionStatus.Connected;
                StatusDetail = $"{LocalAddress}:{LocalPort}";
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
            if (!int.TryParse(LocalPort, out int localPort) || localPort < 1 || localPort > 65535)
            {
                _notificationService.ShowWarn("本地端口须在 1-65535 之间。");
                return;
            }

            try
            {
                _cts = new CancellationTokenSource();
                ConnectionStatus = ConnectionStatus.Connecting;

                string? multicastAddr = ModeIndex == 1 ? MulticastAddress : null;
                int? multicastPort = ModeIndex == 1 && int.TryParse(MulticastPort, out int mp) ? mp : null;

                if (ModeIndex == 1 && !string.IsNullOrEmpty(multicastAddr) && !System.Net.IPAddress.TryParse(multicastAddr, out _))
                {
                    _notificationService.ShowWarn("组播地址格式无效。");
                    return;
                }

                await _udpService.StartAsync(LocalAddress, localPort, multicastAddr, multicastPort, _cts.Token);
                IsRunning = true;

                Log.Append(LogKind.System, $"UDP 已启动 {LocalAddress}:{localPort}");
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
            _udpService.Stop();
            IsRunning = false;
            Log.Append(LogKind.System, "UDP 已停止");
        }

        [RelayCommand]
        private async Task Send()
        {
            if (string.IsNullOrEmpty(SendText)) return;
            if (!IsRunning)
            {
                _notificationService.ShowWarn("请先启动 UDP。");
                return;
            }

            string targetAddr = ModeIndex == 1 ? MulticastAddress : RemoteAddress;
            int targetPort = ModeIndex == 1
                ? (int.TryParse(MulticastPort, out int mp) ? mp : int.Parse(LocalPort))
                : (int.TryParse(RemotePort, out int rp) ? rp : 9090);

            try
            {
                await _udpService.SendAsync(SendText, IsHexSend, targetAddr, targetPort);
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
            if (!IsRunning)
            {
                _notificationService.ShowWarn("请先启动 UDP。");
                return;
            }
            if (IsPolling) return;

            var localCts = new CancellationTokenSource();
            _pollCts = localCts;
            IsPolling = true;
            string targetAddr = ModeIndex == 1 ? MulticastAddress : RemoteAddress;
            int targetPort = ModeIndex == 1
                ? (int.TryParse(MulticastPort, out int mp) ? mp : int.Parse(LocalPort))
                : (int.TryParse(RemotePort, out int rp) ? rp : 9090);
            string targetEncoding = Encoding.UTF8.WebName;
            try
            {
                while (!localCts.Token.IsCancellationRequested)
                {
                    await _udpService.SendAsync(SendText, IsHexSend, targetAddr, targetPort);
                    AppendTxLog(SendText);
                    TxCount++;
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

        partial void OnFrameBreakIntervalChanged(int value)
        {
            _udpService.SetFrameBreakInterval(value);
        }
    }
}