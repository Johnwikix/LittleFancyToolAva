using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.Services;
using FancyToolAva.Utils;

namespace FancyToolAva.ViewModels
{
    /// <summary>
    /// 单个串口会话：一份参数、一份日志、一个物理端口的收发。多路会话由页面 VM 编队。
    /// </summary>
    public partial class SerialSessionViewModel : ViewModelBase
    {
        public static readonly string[] BaudRates =
        [
            "9600", "19200", "38400", "57600",
            "115200", "230400", "460800", "921600"
        ];

        public static readonly string[] Parities = ["None", "Odd", "Even", "Mark", "Space"];
        public static readonly string[] DataBitsList = ["5", "6", "7", "8"];
        public static readonly string[] StopBitsList = ["1", "1.5", "2"];
        public static readonly string[] Encodings = ["Auto", "UTF8", "ASCII", "GB2312"];

        private readonly ISerialPortService _session;
        private readonly INotificationService _notificationService;
        private readonly IFileDialogService _fileDialogService;
        private readonly AppObserveModel _app;

        private CancellationTokenSource? _pollCts;
        private Task? _pollTask;
        private DateTime? _connectedAt;
        private long _pendingRxCount;
        private long _pendingTxCount;
        private Encoding? _cachedEncoding;
        private int _cachedEncodingIndex = -1;
        private bool _isActive = true;

        public string Id { get; } = Guid.NewGuid().ToString("N");

        /// <summary>端口是否被本进程其它会话占用；由页面 VM 注入。</summary>
        public Func<SerialSessionViewModel, string, bool>? PortConflictCheck { get; set; }

        /// <summary>需要提示用户的错误；由页面 VM 决定是否弹窗（只有当前会话才弹）。</summary>
        public event Action<SerialSessionViewModel, string>? Faulted;

        public LogBuffer Log { get; } = new(autoFlush: false);

        public SerialSessionViewModel(ISerialPortService session, INotificationService notificationService,
            IFileDialogService fileDialogService, AppObserveModel app)
        {
            _session = session;
            _notificationService = notificationService;
            _fileDialogService = fileDialogService;
            _app = app;

            _session.BytesReceived += OnBytesReceived;
            _session.DataSent += OnDataSent;
            _session.StatusChanged += OnStatusChanged;
            _session.ConnectionStateChanged += OnConnectionStateChanged;
        }

        public string SelectedPort
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(ParameterSummary));
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        } = string.Empty;

        public string Alias
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        } = string.Empty;

        public int BaudRateIndex
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(ParameterSummary));
                }
            }
        }

        public int ParityIndex
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(ParameterSummary));
                }
            }
        }

        public int DataBitsIndex
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(ParameterSummary));
                }
            }
        } = 3;

        public int StopBitsIndex
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(ParameterSummary));
                }
            }
        }

        public int EncodingIndex
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (value == 0) IsHexDisplay = false;
                }
            }
        }

        public string SendText
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string StatusText
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string StatusDetail
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

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

        public bool IsPolling
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int PollInterval
        {
            get;
            set => SetProperty(ref field, value);
        } = 1000;

        public int FrameBreakInterval
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    _session.SetFrameBreakInterval(value);
                }
            }
        } = 20;

        public bool IsRtsEnabled
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) ApplyHandshake();
            }
        }

        public bool IsDtrEnabled
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) ApplyHandshake();
            }
        }

        public bool IsConnected
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) OnIsConnectedChanged(value);
            }
        }

        public ConnectionStatus ConnectionStatus
        {
            get;
            set => SetProperty(ref field, value);
        } = ConnectionStatus.Idle;

        public int RxCount
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(RxTxSummary));
                }
            }
        }

        public int TxCount
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(RxTxSummary));
                }
            }
        }

        public int UnreadCount
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(HasUnread));
                    OnPropertyChanged(nameof(UnreadText));
                }
            }
        }

        public bool HasUnread => UnreadCount > 0;

        public string UnreadText => UnreadCount > 99 ? "99+" : UnreadCount.ToString();

        public string ElapsedText
        {
            get;
            set => SetProperty(ref field, value);
        } = "00:00:00";

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

        public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? SelectedPort : Alias;

        /// <summary>形如 "115200 8N1"，用于 ComboBox 折叠态与下拉项的第二行。</summary>
        public string ParameterSummary
        {
            get
            {
                string parity = ParityIndex switch
                {
                    1 => "O",
                    2 => "E",
                    3 => "M",
                    4 => "S",
                    _ => "N"
                };
                string stop = StopBitsIndex switch
                {
                    1 => "1.5",
                    2 => "2",
                    _ => "1"
                };
                return $"{BaudRates[Math.Clamp(BaudRateIndex, 0, BaudRates.Length - 1)]} {DataBitsList[Math.Clamp(DataBitsIndex, 0, DataBitsList.Length - 1)]}{parity}{stop}";
            }
        }

        public string RxTxSummary => $"RX {ShortCount(RxCount)} · TX {ShortCount(TxCount)}";

        public string StateText => ConnectionStatus switch
        {
            ConnectionStatus.Connected => LocalizationRegistry.Get("Common.Status_Connected"),
            ConnectionStatus.Connecting => LocalizationRegistry.Get("Common.Status_Connecting"),
            ConnectionStatus.Error => LocalizationRegistry.Get("Common.Status_Error"),
            _ => LocalizationRegistry.Get("Common.Status_Idle")
        };

        private static string ShortCount(int value)
        {
            return value >= 1_000_000
                ? $"{value / 1_000_000.0:0.#}M"
                : value >= 1_000
                    ? $"{value / 1_000.0:0.#}K"
                    : value.ToString();
        }

        public void MarkRead()
        {
            if (UnreadCount == 0) return;
            UnreadCount = 0;
        }

        /// <summary>当前会话放大日志上限，非当前会话降级，控制多路内存占用。</summary>
        public void SetActive(bool active)
        {
            _isActive = active;
            Log.MaxLines = active ? 20_000 : 5_000;
        }

        internal void FlushCounters()
        {
            int tx = (int)Interlocked.Exchange(ref _pendingTxCount, 0);
            int rx = (int)Interlocked.Exchange(ref _pendingRxCount, 0);
            if (tx > 0) TxCount += tx;
            if (rx > 0) RxCount += rx;
        }

        internal void FlushLog() => Log.Flush();

        internal void UpdateElapsed()
        {
            if (_connectedAt is { } start)
            {
                ElapsedText = (DateTime.Now - start).ToString(@"hh\:mm\:ss");
            }
        }

        public SerialSessionViewState ToState() => new()
        {
            Id = Id,
            Alias = Alias,
            PortName = SelectedPort,
            BaudRateIndex = BaudRateIndex,
            ParityIndex = ParityIndex,
            DataBitsIndex = DataBitsIndex,
            StopBitsIndex = StopBitsIndex,
            EncodingIndex = EncodingIndex,
            FrameBreakInterval = FrameBreakInterval,
            IsHexSend = IsHexSend,
            IsHexDisplay = IsHexDisplay,
            IsRtsEnabled = IsRtsEnabled,
            IsDtrEnabled = IsDtrEnabled,
            PollInterval = PollInterval,
            EnableSendCount = EnableSendCount,
            SendCount = SendCount,
            SendText = SendText
        };

        public void ApplyState(SerialSessionViewState state)
        {
            Alias = state.Alias;
            SelectedPort = state.PortName;
            BaudRateIndex = Math.Clamp(state.BaudRateIndex, 0, BaudRates.Length - 1);
            ParityIndex = Math.Clamp(state.ParityIndex, 0, Parities.Length - 1);
            DataBitsIndex = Math.Clamp(state.DataBitsIndex, 0, DataBitsList.Length - 1);
            StopBitsIndex = Math.Clamp(state.StopBitsIndex, 0, StopBitsList.Length - 1);
            EncodingIndex = Math.Clamp(state.EncodingIndex, 0, Encodings.Length - 1);
            FrameBreakInterval = state.FrameBreakInterval <= 0 ? 20 : state.FrameBreakInterval;
            IsHexSend = state.IsHexSend;
            IsHexDisplay = state.IsHexDisplay;
            IsRtsEnabled = state.IsRtsEnabled;
            IsDtrEnabled = state.IsDtrEnabled;
            PollInterval = state.PollInterval <= 0 ? 1000 : state.PollInterval;
            EnableSendCount = state.EnableSendCount;
            SendCount = state.SendCount <= 0 ? 1 : state.SendCount;
            SendText = state.SendText;
        }

        private void ApplyHandshake()
        {
            if (!IsConnected) return;
            _session.SetHandshake(IsRtsEnabled, IsDtrEnabled);
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
                Log.EnqueueLine(LogKind.Rx, DecodeBytes(bytes));
            }

            if (!_isActive)
            {
                var next = UnreadCount + 1;
                if (next > 999) next = 999;
                UnreadCount = next;
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
                Log.EnqueueLine(LogKind.Tx, GetEncodingCached().GetString(bytes));
            }
        }

        private Encoding GetEncodingCached()
        {
            int idx = Math.Clamp(EncodingIndex, 0, Encodings.Length - 1);
            if (idx == _cachedEncodingIndex && _cachedEncoding is not null) return _cachedEncoding;
            _cachedEncodingIndex = idx;
            _cachedEncoding = ToolMethod.GetEncoding(ParseEncodingMode(Encodings[idx]));
            return _cachedEncoding;
        }

        private string DecodeBytes(byte[] bytes)
        {
            try
            {
                return GetEncodingCached().GetString(bytes);
            }
            catch
            {
                return ToolMethod.ByteArrayToHexString(bytes);
            }
        }

        private static ToolMethod.EncodingMode ParseEncodingMode(string encoding)
        {
            return encoding switch
            {
                "UTF8" => ToolMethod.EncodingMode.UTF8,
                "ASCII" => ToolMethod.EncodingMode.ASCII,
                "GB2312" => ToolMethod.EncodingMode.GB2312,
                _ => ToolMethod.EncodingMode.Auto
            };
        }

        private void OnStatusChanged(string status)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = status;
                OnPropertyChanged(nameof(StatusText));
            });
        }

        private void OnConnectionStateChanged(object? sender, ConnectionEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (e.Type)
                {
                    case ConnectionEventType.Connected:
                        IsConnected = true;
                        Log.Append(LogKind.System, e.Message);
                        break;
                    case ConnectionEventType.Disconnected:
                        IsConnected = false;
                        IsPolling = false;
                        _pollCts?.Cancel();
                        Log.Append(LogKind.System, e.Message);
                        break;
                    case ConnectionEventType.Lost:
                        IsConnected = false;
                        IsPolling = false;
                        _pollCts?.Cancel();
                        ConnectionStatus = ConnectionStatus.Error;
                        Log.Append(LogKind.Error, e.Message);
                        Faulted?.Invoke(this, e.Message);
                        break;
                    case ConnectionEventType.LineDisconnect:
                        Log.Append(LogKind.Warn, e.Message);
                        break;
                    case ConnectionEventType.Error:
                        if (!IsConnected) ConnectionStatus = ConnectionStatus.Error;
                        Log.Append(LogKind.Error, e.Message);
                        break;
                }
            });
        }

        private void OnIsConnectedChanged(bool value)
        {
            if (value)
            {
                ConnectionStatus = ConnectionStatus.Connected;
                StatusDetail = $"{SelectedPort} @ {BaudRates[BaudRateIndex]}";
                _connectedAt = DateTime.Now;
                ElapsedText = "00:00:00";
                ApplyHandshake();
            }
            else
            {
                if (ConnectionStatus != ConnectionStatus.Error)
                {
                    ConnectionStatus = ConnectionStatus.Idle;
                }
                StatusDetail = string.Empty;
                _connectedAt = null;
                ElapsedText = "00:00:00";
                _pollCts?.Cancel();
                _pollTask = null;
            }
        }

        [RelayCommand]
        private async Task Connect()
        {
            if (string.IsNullOrEmpty(SelectedPort))
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Serial.Msg_PortNotSelected"));
                return;
            }

            if (PortConflictCheck?.Invoke(this, SelectedPort) == true)
            {
                var conflict = LocalizationRegistry.Get("Serial.Msg_PortInUseBySession", SelectedPort);
                Log.Append(LogKind.Error, conflict);
                ConnectionStatus = ConnectionStatus.Error;
                _notificationService.ShowWarn(conflict);
                return;
            }

            int timeoutSec = Math.Max(1, _app.Preferences.ConnectionTimeoutSec);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                int baudRate = int.Parse(BaudRates[BaudRateIndex]);
                Parity parity = (Parity)ParityIndex;
                int dataBits = int.Parse(DataBitsList[DataBitsIndex]);
                StopBits stopBits = StopBitsIndex switch
                {
                    0 => StopBits.One,
                    1 => StopBits.OnePointFive,
                    2 => StopBits.Two,
                    _ => StopBits.One
                };

                ConnectionStatus = ConnectionStatus.Connecting;
                await _session.OpenAsync(SelectedPort, baudRate, parity, dataBits, stopBits, cts.Token);
                IsConnected = _session.IsOpen;
                if (IsConnected)
                {
                    Log.Append(LogKind.System, LocalizationRegistry.Get("Serial.Status_RealtimeConnected", SelectedPort, baudRate));
                }
            }
            catch (OperationCanceledException)
            {
                ConnectionStatus = ConnectionStatus.Idle;
                IsConnected = false;
                var timeoutMsg = LocalizationRegistry.Get("Serial.Status_Timeout", timeoutSec);
                Log.Append(LogKind.Warn, timeoutMsg);
                Faulted?.Invoke(this, timeoutMsg);
            }
            catch (Exception ex)
            {
                ConnectionStatus = ConnectionStatus.Error;
                IsConnected = false;
                var errMsg = LocalizationRegistry.Get("Serial.Status_ConnectFail", ex.Message);
                Log.Append(LogKind.Error, errMsg);
                Faulted?.Invoke(this, errMsg);
            }
        }

        [RelayCommand]
        private void Disconnect()
        {
            _pollCts?.Cancel();
            _session.Close();
            IsConnected = _session.IsOpen;
            IsPolling = false;
        }

        [RelayCommand]
        private Task Send() => SendCurrentAsync();

        public async Task SendCurrentAsync()
        {
            if (string.IsNullOrEmpty(SendText)) return;
            string encoding = Encodings[Math.Clamp(EncodingIndex, 0, Encodings.Length - 1)];
            try
            {
                await _session.SendAsync(SendText, IsHexSend, encoding).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var errMsg = LocalizationRegistry.Get("Serial.Status_SendFail", ex.Message);
                Log.Enqueue(LogKind.Error, errMsg);
                Faulted?.Invoke(this, errMsg);
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
            await LogFileHelper.SaveAsync(Log, _fileDialogService, _notificationService, "Serial");
        }

        [RelayCommand]
        private void ToggleRts()
        {
            ApplyHandshake();
        }

        [RelayCommand]
        private void ToggleDtr()
        {
            ApplyHandshake();
        }

        [RelayCommand]
        private void StartPolling()
        {
            if (!IsConnected)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Serial.Msg_NotConnected"));
                return;
            }
            if (IsPolling) return;

            var localCts = new CancellationTokenSource();
            _pollCts = localCts;
            IsPolling = true;
            string encoding = Encodings[Math.Clamp(EncodingIndex, 0, Encodings.Length - 1)];

            _pollTask = Task.Run(async () =>
            {
                int sentCount = 0;
                long intervalTicks = Math.Max(1L, (long)(PollInterval * (Stopwatch.Frequency / 1000.0)));
                long nextDue = Stopwatch.GetTimestamp() + intervalTicks;
                try
                {
                    while (!localCts.Token.IsCancellationRequested)
                    {
                        if (!IsConnected) break;
                        try
                        {
                            await _session.SendAsync(SendText, IsHexSend, encoding).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Log.Enqueue(LogKind.Error, LocalizationRegistry.Get("Serial.Msg_TimedSendError", ex.Message));
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

        public void Shutdown()
        {
            _pollCts?.Cancel();
            OnPropertyChanged(nameof(StatusText));
            _session.BytesReceived -= OnBytesReceived;
            _session.DataSent -= OnDataSent;
            _session.StatusChanged -= OnStatusChanged;
            _session.ConnectionStateChanged -= OnConnectionStateChanged;
        }
    }
}