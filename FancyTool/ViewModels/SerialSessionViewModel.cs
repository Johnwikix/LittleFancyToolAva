using System.IO.Ports;
using System.Text;
using Avalonia.Threading;
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
    public partial class SerialSessionViewModel : TerminalSessionViewModelBase
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
        private readonly IFileDialogService _fileDialogService;
        private readonly AppObserveModel _app;
        private Encoding? _cachedEncoding;
        private int _cachedEncodingIndex = -1;

        /// <summary>端口是否被本进程其它会话占用；由页面 VM 注入。</summary>
        public Func<SerialSessionViewModel, string, bool>? PortConflictCheck { get; set; }

        public SerialSessionViewModel(ISerialPortService session, INotificationService notificationService,
            IFileDialogService fileDialogService, AppObserveModel app) : base(notificationService)
        {
            _session = session;
            _fileDialogService = fileDialogService;
            _app = app;

            _session.BytesReceived += OnBytesReceived;
            _session.DataSent += OnDataSent;
            _session.StatusChanged += OnStatusChanged;
            _session.ConnectionStateChanged += OnConnectionStateChanged;
        }

        public override bool IsActive => IsConnected;

        public override string DisplayName => string.IsNullOrWhiteSpace(Alias) ? SelectedPort : Alias;

        protected override string NotActiveMessageKey => "Serial.Msg_NotConnected";

        protected override string TimedSendErrorMessageKey => "Serial.Msg_TimedSendError";

        public string SelectedPort
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
            set => SetProperty(ref field, value);
        }

        public int ParityIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int DataBitsIndex
        {
            get;
            set => SetProperty(ref field, value);
        } = 3;

        public int StopBitsIndex
        {
            get;
            set => SetProperty(ref field, value);
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
            RestoreId(state.Id);
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

        private void OnBytesReceived(byte[] bytes) => OnReceived(bytes);

        private void OnDataSent(byte[] bytes) => OnSent(bytes);

        protected override string DecodeBytes(byte[] bytes)
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

        private Encoding GetEncodingCached()
        {
            int idx = Math.Clamp(EncodingIndex, 0, Encodings.Length - 1);
            if (idx == _cachedEncodingIndex && _cachedEncoding is not null) return _cachedEncoding;
            _cachedEncodingIndex = idx;
            _cachedEncoding = ToolMethod.GetEncoding(ParseEncodingMode(Encodings[idx]));
            return _cachedEncoding;
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
            PostStatusText(status);
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
                        CancelPolling();
                        Log.Append(LogKind.System, e.Message);
                        break;
                    case ConnectionEventType.Lost:
                        IsConnected = false;
                        IsPolling = false;
                        CancelPolling();
                        ConnectionStatus = ConnectionStatus.Error;
                        Log.Append(LogKind.Error, e.Message);
                        RaiseFault(e.Message);
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
            OnPropertyChanged(nameof(IsActive));
            if (value)
            {
                ConnectionStatus = ConnectionStatus.Connected;
                StatusDetail = $"{SelectedPort} @ {BaudRates[BaudRateIndex]}";
                StartElapsed();
                ApplyHandshake();
            }
            else
            {
                if (ConnectionStatus != ConnectionStatus.Error)
                {
                    ConnectionStatus = ConnectionStatus.Idle;
                }
                StatusDetail = string.Empty;
                StopElapsed();
                CancelPolling();
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
                RaiseFault(timeoutMsg);
            }
            catch (Exception ex)
            {
                ConnectionStatus = ConnectionStatus.Error;
                IsConnected = false;
                var errMsg = LocalizationRegistry.Get("Serial.Status_ConnectFail", ex.Message);
                Log.Append(LogKind.Error, errMsg);
                RaiseFault(errMsg);
            }
        }

        [RelayCommand]
        private void Disconnect()
        {
            CancelPolling();
            _session.Close();
            IsConnected = _session.IsOpen;
            IsPolling = false;
        }

        public override async Task SendManualAsync()
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
                RaiseFault(errMsg);
            }
        }

        protected override Func<Task> CreatePollingSender()
        {
            string encoding = Encodings[Math.Clamp(EncodingIndex, 0, Encodings.Length - 1)];
            return () => _session.SendAsync(SendText, IsHexSend, encoding);
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

        public override void Shutdown()
        {
            base.Shutdown();
            _session.BytesReceived -= OnBytesReceived;
            _session.DataSent -= OnDataSent;
            _session.StatusChanged -= OnStatusChanged;
            _session.ConnectionStateChanged -= OnConnectionStateChanged;
        }
    }
}
