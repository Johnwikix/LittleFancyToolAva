using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lang.Avalonia;
using FancyToolAva.Models;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.Services;
using FancyToolAva.Utils;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.ViewModels
{
    public partial class TcpServerViewModel : ViewModelBase, IViewState, IViewLifecycle
    {
        private readonly ITcpServerService _tcpService;
        private readonly INotificationService _notificationService;
        private readonly IViewStateService _viewStateService;
        private readonly IFileDialogService _fileDialogService;
        private readonly AppObserveModel _app;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _pollCts;
        private Task? _pollTask;
        private DispatcherTimer? _elapsedTimer;
        private DispatcherTimer? _uiFlushTimer;
        private DateTime? _startedAt;
        private long _pendingRxCount;
        private long _pendingTxCount;

        public ObservableCollection<string> Modes { get; } = ["", ""];

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
        } = "";

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

        public TcpServerViewModel(ITcpServerService tcpService, INotificationService notificationService, IViewStateService viewStateService, IFileDialogService fileDialogService, AppObserveModel app)
        {
            _tcpService = tcpService;
            _notificationService = notificationService;
            _viewStateService = viewStateService;
            _fileDialogService = fileDialogService;
            _app = app;
            _tcpService.BytesReceived += OnBytesReceived;
            _tcpService.DataSent += OnDataSent;
            _tcpService.StatusChanged += OnStatusChanged;
            _tcpService.ConnectionStateChanged += OnConnectionStateChanged;
            _viewStateService.Register(this);

            Modes[0] = LocalizationRegistry.Get("Tcp.Mode_Server");
            Modes[1] = LocalizationRegistry.Get("Tcp.Mode_Client");
            StatusText = LocalizationRegistry.Get("Tcp.Status_Ready");

            I18nManager.Instance.CultureChanged += OnCultureChanged;
        }

        private void OnCultureChanged(object? sender, EventArgs e)
        {
            Modes[0] = LocalizationRegistry.Get("Tcp.Mode_Server");
            Modes[1] = LocalizationRegistry.Get("Tcp.Mode_Client");
            StatusText = LocalizationRegistry.Get(ModeIndex == 0 ? "Tcp.Status_ReadyServer" : "Tcp.Status_ReadyClient");
            OnPropertyChanged(nameof(StatusDetail));
        }

        void IViewLifecycle.OnNavigatedTo() { }

        void IViewLifecycle.OnNavigatedFrom() { }

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
            Interlocked.Increment(ref _pendingTxCount);
            if (IsHexDisplay)
            {
                Log.Enqueue(LogKind.Tx, ToolMethod.ByteArrayToSpacedHexString(bytes));
            }
            else
            {
                Log.EnqueueLine(LogKind.Tx, Encoding.UTF8.GetString(bytes));
            }
        }

        private void OnBytesReceived(byte[] bytes)
        {
            Interlocked.Increment(ref _pendingRxCount);

            if (IsHexDisplay)
            {
                Log.Enqueue(LogKind.Rx, ToolMethod.ByteArrayToSpacedHexString(bytes));
            }
            else
            {
                Log.EnqueueLine(LogKind.Rx, Encoding.UTF8.GetString(bytes));
            }
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

        private void StartUiFlushTimer()
        {
            StopUiFlushTimer();
            _uiFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _uiFlushTimer.Tick += (_, _) =>
            {
                int txDelta = (int)Interlocked.Exchange(ref _pendingTxCount, 0);
                int rxDelta = (int)Interlocked.Exchange(ref _pendingRxCount, 0);
                if (txDelta > 0) TxCount += txDelta;
                if (rxDelta > 0) RxCount += rxDelta;
            };
            _uiFlushTimer.Start();
        }

        private void StopUiFlushTimer()
        {
            _uiFlushTimer?.Stop();
            _uiFlushTimer = null;
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
                    ? LocalizationRegistry.Get("Tcp.Status_Server", Address, Port)
                    : LocalizationRegistry.Get("Tcp.Status_Client", Address, Port);
                StartElapsedTimer();
                StartUiFlushTimer();
            }
            else
            {
                ConnectionStatus = ConnectionStatus.Idle;
                StatusDetail = string.Empty;
                StopElapsedTimer();
                _pollCts?.Cancel();
                var task = _pollTask;
                if (task != null) { try { task.Wait(200); } catch { } }
                _pollTask = null;
                StopUiFlushTimer();
            }
        }

        [RelayCommand]
        private async Task Start()
        {
            if (!int.TryParse(Port, out int portNum) || portNum < 1 || portNum > 65535)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Tcp.Msg_InvalidPort"));
                return;
            }

            int timeoutSec = Math.Max(1, _app.Preferences.ConnectionTimeoutSec);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                ConnectionStatus = ConnectionStatus.Connecting;

                if (ModeIndex == 0)
                {
                    await _tcpService.StartServerAsync(Address, portNum, cts.Token);
                    IsRunning = _tcpService.IsRunning;
                }
                else
                {
                    await _tcpService.ConnectClientAsync(Address, portNum, cts.Token);
                    IsConnected = true;
                }

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
            catch (OperationCanceledException)
            {
                ConnectionStatus = ConnectionStatus.Idle;
                IsRunning = false;
                IsConnected = false;
                var timeoutMsg = LocalizationRegistry.Get("Tcp.Msg_Timeout", timeoutSec);
                Log.Append(LogKind.Warn, timeoutMsg);
                _notificationService.ShowWarn(timeoutMsg);
            }
            catch (Exception ex)
            {
                ConnectionStatus = ConnectionStatus.Error;
                IsRunning = false;
                IsConnected = false;
                var failMsg = LocalizationRegistry.Get("Tcp.Msg_StartFail", ex.Message);
                Log.Append(LogKind.Error, failMsg);
                _notificationService.ShowError(failMsg);
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
        }

        [RelayCommand]
        private void DisconnectClient()
        {
            if (string.IsNullOrEmpty(SelectedClient))
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Tcp.Msg_NoClientSelected"));
                return;
            }
            _tcpService.DisconnectClient(SelectedClient);
            SelectedClient = string.Empty;
        }

        [RelayCommand]
        private async Task Send()
        {
            if (string.IsNullOrEmpty(SendText)) return;

            if (ModeIndex == 0 && !IsRunning)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Tcp.Msg_ServerNotStarted"));
                return;
            }
            if (ModeIndex != 0 && !IsConnected)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Tcp.Msg_ClientNotConnected"));
                return;
            }

            string? target = ModeIndex == 0 ? SelectedClient : null;
            if (ModeIndex == 0 && string.IsNullOrEmpty(target) && ConnectedClients.Count > 0)
            {
                target = null;
            }

            try
            {
                await _tcpService.SendAsync(SendText, IsHexSend, target).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var errMsg = LocalizationRegistry.Get("Tcp.Msg_SendFail", ex.Message);
                Log.Enqueue(LogKind.Error, errMsg);
                Dispatcher.UIThread.Post(() => _notificationService.ShowError(errMsg));
            }
        }

        [RelayCommand]
        private void Clear()
        {
            Log.Clear();
            Interlocked.Exchange(ref _pendingRxCount, 0);
            Interlocked.Exchange(ref _pendingTxCount, 0);
            RxCount = 0;
            TxCount = 0;
        }

        [RelayCommand]
        private async Task SaveData()
        {
            await LogFileHelper.SaveAsync(Log, _fileDialogService, _notificationService, "Tcp");
        }

        [RelayCommand]
        private void StartPolling()
        {
            if (!IsRunning && !IsConnected)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Tcp.Msg_NotActive"));
                return;
            }
            if (IsPolling) return;

            var localCts = new CancellationTokenSource();
            _pollCts = localCts;
            IsPolling = true;
            StartUiFlushTimer();

            _pollTask = Task.Run(async () =>
            {
                int sentCount = 0;
                long intervalTicks = Math.Max(1L, (long)(PollInterval * (Stopwatch.Frequency / 1000.0)));
                long nextDue = Stopwatch.GetTimestamp() + intervalTicks;
                try
                {
                    while (!localCts.Token.IsCancellationRequested)
                    {
                        if (!IsRunning && !IsConnected) break;
                        try
                        {
                            await _tcpService.SendAsync(SendText, IsHexSend, null).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Log.Enqueue(LogKind.Error, LocalizationRegistry.Get("Tcp.Msg_TimedSendError", ex.Message));
                        }
                        if (EnableSendCount && ++sentCount >= SendCount) break;

                        nextDue += intervalTicks;
                        long remainingTicks = nextDue - Stopwatch.GetTimestamp();
                        while (remainingTicks > 0)
                        {
                            if (localCts.Token.IsCancellationRequested) break;
                            long remainingMs = remainingTicks * 1000 / Stopwatch.Frequency;
                            if (remainingMs >= 1)
                                await Task.Delay((int)remainingMs, localCts.Token).ConfigureAwait(false);
                            else
                                await Task.Yield();
                            remainingTicks = nextDue - Stopwatch.GetTimestamp();
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (ReferenceEquals(_pollCts, localCts))
                        {
                            IsPolling = false;
                        }
                    });
                }
            }, localCts.Token);
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
                StatusText = LocalizationRegistry.Get("Tcp.Status_ReadyServer");
            }
            else
            {
                _tcpService.StopServer();
                IsRunning = false;
                StatusText = LocalizationRegistry.Get("Tcp.Status_ReadyClient");
            }
        }
    }
}
