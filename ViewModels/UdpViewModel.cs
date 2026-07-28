using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lang.Avalonia;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.Utils;
using Microsoft.Extensions.Logging;

namespace LittleFancyToolAva.ViewModels
{
    public partial class UdpViewModel : ViewModelBase, IViewState, IViewLifecycle
    {
        private readonly IUdpService _udpService;
        private readonly INotificationService _notificationService;
        private readonly IViewStateService _viewStateService;
        private readonly AppObserveModel _app;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _pollCts;
        private Task? _pollTask;
        private DispatcherTimer? _elapsedTimer;
        private DispatcherTimer? _uiFlushTimer;
        private DateTime? _startedAt;
        private long _pendingRxCount;
        private long _pendingTxCount;

        public LogBuffer Log { get; } = new();

        public int ModeIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        public string LocalAddress
        {
            get;
            set => SetProperty(ref field, value);
        } = "0.0.0.0";

        public string LocalPort
        {
            get;
            set => SetProperty(ref field, value);
        } = "8080";

        public string MulticastAddress
        {
            get;
            set => SetProperty(ref field, value);
        } = "239.0.0.1";

        public string MulticastPort
        {
            get;
            set => SetProperty(ref field, value);
        } = "8080";

        public string RemoteAddress
        {
            get;
            set => SetProperty(ref field, value);
        } = "127.0.0.1";

        public string RemotePort
        {
            get;
            set => SetProperty(ref field, value);
        } = "9090";

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

        public bool IsAnyActive => IsRunning;

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

        string IViewState.ViewName => "udpView";

        public UdpViewModel(IUdpService udpService, INotificationService notificationService, IViewStateService viewStateService, AppObserveModel app)
        {
            _udpService = udpService;
            _notificationService = notificationService;
            _viewStateService = viewStateService;
            _app = app;
            _udpService.BytesReceived += OnBytesReceived;
            _udpService.DataSent += OnDataSent;
            _udpService.StatusChanged += OnStatusChanged;
            _udpService.ConnectionStateChanged += OnConnectionStateChanged;
            _viewStateService.Register(this);

            StatusText = LocalizationRegistry.Get("Udp.Status_Ready");

            I18nManager.Instance.CultureChanged += (_, _) =>
            {
                StatusText = LocalizationRegistry.Get("Udp.Status_Ready");
            };
        }

        void IViewLifecycle.OnNavigatedTo() { }

        void IViewLifecycle.OnNavigatedFrom() { }

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
            PollInterval = PollInterval,
            EnableSendCount = EnableSendCount,
            SendCount = SendCount
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
            Dispatcher.UIThread.Post(() => StatusText = status);
        }

        private void OnConnectionStateChanged(object? sender, ConnectionEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (e.Type)
                {
                    case ConnectionEventType.Connected:
                        IsRunning = true;
                        Log.Append(LogKind.System, e.Message);
                        break;
                    case ConnectionEventType.Disconnected:
                        IsRunning = false;
                        IsPolling = false;
                        _pollCts?.Cancel();
                        Log.Append(LogKind.System, e.Message);
                        break;
                    case ConnectionEventType.Lost:
                        IsRunning = false;
                        IsPolling = false;
                        _pollCts?.Cancel();
                        Log.Append(LogKind.Error, e.Message);
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
            if (value)
            {
                ConnectionStatus = ConnectionStatus.Connected;
                StatusDetail = $"{LocalAddress}:{LocalPort}";
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
            if (!int.TryParse(LocalPort, out int localPort) || localPort < 1 || localPort > 65535)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Udp.Msg_InvalidLocalPort"));
                return;
            }

            int timeoutSec = Math.Max(1, _app.Preferences.ConnectionTimeoutSec);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                ConnectionStatus = ConnectionStatus.Connecting;

                string? multicastAddr = ModeIndex == 1 ? MulticastAddress : null;
                int? multicastPort = ModeIndex == 1 && int.TryParse(MulticastPort, out int mp) ? mp : null;

                if (ModeIndex == 1 && !string.IsNullOrEmpty(multicastAddr) && !System.Net.IPAddress.TryParse(multicastAddr, out _))
                {
                    _notificationService.ShowWarn(LocalizationRegistry.Get("Udp.Msg_InvalidMulticast"));
                    ConnectionStatus = ConnectionStatus.Idle;
                    return;
                }

                await _udpService.StartAsync(LocalAddress, localPort, multicastAddr, multicastPort, cts.Token);
                IsRunning = true;

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
            catch (OperationCanceledException)
            {
                ConnectionStatus = ConnectionStatus.Idle;
                IsRunning = false;
                var timeoutMsg = LocalizationRegistry.Get("Udp.Msg_StartTimeout", timeoutSec);
                Log.Append(LogKind.Warn, timeoutMsg);
                _notificationService.ShowWarn(timeoutMsg);
            }
            catch (Exception ex)
            {
                ConnectionStatus = ConnectionStatus.Error;
                IsRunning = false;
                var failMsg = LocalizationRegistry.Get("Udp.Msg_StartFail", ex.Message);
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
            _udpService.Stop();
            IsRunning = false;
        }

        [RelayCommand]
        private async Task Send()
        {
            if (string.IsNullOrEmpty(SendText)) return;
            if (!IsRunning)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Udp.Msg_NotStarted"));
                return;
            }

            string targetAddr = ModeIndex == 1 ? MulticastAddress : RemoteAddress;
            int targetPort = ModeIndex == 1
                ? (int.TryParse(MulticastPort, out int mp) ? mp : int.Parse(LocalPort))
                : (int.TryParse(RemotePort, out int rp) ? rp : 9090);

            try
            {
                await _udpService.SendAsync(SendText, IsHexSend, targetAddr, targetPort).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var errMsg = LocalizationRegistry.Get("Udp.Msg_SendFail", ex.Message);
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
        private void StartPolling()
        {
            if (!IsRunning)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Udp.Msg_NotStarted"));
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
                        if (!IsRunning) break;
                        try
                        {
                            await _udpService.SendAsync(SendText, IsHexSend, targetAddr, targetPort).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Log.Enqueue(LogKind.Error, LocalizationRegistry.Get("Udp.Msg_TimedSendError", ex.Message));
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
            _udpService.SetFrameBreakInterval(value);
        }
    }
}
