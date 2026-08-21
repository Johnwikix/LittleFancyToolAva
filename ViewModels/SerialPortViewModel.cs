using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Ports;
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
    public partial class SerialPortViewModel : ViewModelBase, IViewState, IViewLifecycle
    {
        private readonly ISerialPortService _serialPortService;
        private readonly INotificationService _notificationService;
        private readonly IViewStateService _viewStateService;
        private readonly IFileDialogService _fileDialogService;
        private readonly AppObserveModel _app;
        private CancellationTokenSource? _pollCts;
        private Task? _pollTask;
        private DispatcherTimer? _elapsedTimer;
        private DispatcherTimer? _uiFlushTimer;
        private DateTime? _connectedAt;
        private long _pendingRxCount;
        private long _pendingTxCount;

        public ObservableCollection<string> PortNames { get; } = [];
        public ObservableCollection<string> BaudRates { get; } =
        [
            "9600", "19200", "38400", "57600",
            "115200", "230400", "460800", "921600"
        ];
        public ObservableCollection<string> Parities { get; } =
            ["None", "Odd", "Even", "Mark", "Space"];
        public ObservableCollection<string> DataBitsList { get; } =
            ["5", "6", "7", "8"];
        public ObservableCollection<string> StopBitsList { get; } =
            ["1", "1.5", "2"];
        public ObservableCollection<string> Encodings { get; } =
            ["Auto", "UTF8", "ASCII", "GB2312"];

        public LogBuffer Log { get; } = new();

        public string SelectedPort
        {
            get;
            set => SetProperty(ref field, value);
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
                    OnEncodingIndexChanged(value);
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
                    OnFrameBreakIntervalChanged(value);
                }
            }
        } = 20;

        public bool IsRtsEnabled
        {
            get;
            set => SetProperty(ref field, value);
        }

        public bool IsDtrEnabled
        {
            get;
            set => SetProperty(ref field, value);
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

        string IViewState.ViewName => "serialPortView";

        public SerialPortViewModel(ISerialPortService serialPortService, INotificationService notificationService, IViewStateService viewStateService, IFileDialogService fileDialogService, AppObserveModel app)
        {
            _serialPortService = serialPortService;
            _notificationService = notificationService;
            _viewStateService = viewStateService;
            _fileDialogService = fileDialogService;
            _app = app;
            _serialPortService.BytesReceived += OnBytesReceived;
            _serialPortService.DataSent += OnDataSent;
            _serialPortService.StatusChanged += OnStatusChanged;
            _serialPortService.ConnectionStateChanged += OnConnectionStateChanged;
            RefreshPorts();
            _viewStateService.Register(this);
        }

        void IViewLifecycle.OnNavigatedTo() { }

        void IViewLifecycle.OnNavigatedFrom() { }

        object IViewState.CaptureState() => new SerialPortViewState
        {
            SelectedPort = SelectedPort,
            BaudRateIndex = BaudRateIndex,
            ParityIndex = ParityIndex,
            DataBitsIndex = DataBitsIndex,
            StopBitsIndex = StopBitsIndex,
            EncodingIndex = EncodingIndex,
            SendText = SendText,
            IsHexSend = IsHexSend,
            IsHexDisplay = IsHexDisplay,
            IsRtsEnabled = IsRtsEnabled,
            IsDtrEnabled = IsDtrEnabled,
            PollInterval = PollInterval,
            FrameBreakInterval = FrameBreakInterval,
            EnableSendCount = EnableSendCount,
            SendCount = SendCount
        };

        void IViewState.RestoreState(object state)
        {
            if (state is SerialPortViewState s)
            {
                SelectedPort = s.SelectedPort;
                BaudRateIndex = s.BaudRateIndex;
                ParityIndex = s.ParityIndex;
                DataBitsIndex = s.DataBitsIndex;
                StopBitsIndex = s.StopBitsIndex;
                EncodingIndex = s.EncodingIndex;
                SendText = s.SendText;
                IsHexSend = s.IsHexSend;
                IsHexDisplay = s.IsHexDisplay;
                IsRtsEnabled = s.IsRtsEnabled;
                IsDtrEnabled = s.IsDtrEnabled;
                PollInterval = s.PollInterval;
                FrameBreakInterval = s.FrameBreakInterval;
                EnableSendCount = s.EnableSendCount;
                SendCount = s.SendCount;
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
                Log.EnqueueLine(LogKind.Rx, DecodeBytes(bytes));
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

        private Encoding? _cachedEncoding;
        private int _cachedEncodingIndex = -1;

        private Encoding GetEncodingCached()
        {
            int idx = EncodingIndex;
            if (idx == _cachedEncodingIndex) return _cachedEncoding!;
            string encoding = Encodings[idx];
            _cachedEncodingIndex = idx;
            _cachedEncoding = ToolMethod.GetEncoding(ParseEncodingMode(encoding));
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
                        Log.Append(LogKind.Error, e.Message);
                        _notificationService.ShowError(e.Message);
                        break;
                    case ConnectionEventType.LineDisconnect:
                        Log.Append(LogKind.Warn, e.Message);
                        break;
                    case ConnectionEventType.Error:
                        ConnectionStatus = ConnectionStatus.Error;
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
                StartElapsedTimer();
                StartUiFlushTimer();
            }
            else
            {
                ConnectionStatus = ConnectionStatus.Idle;
                StatusDetail = string.Empty;
                StopElapsedTimer();
                _connectedAt = null;
                ElapsedText = "00:00:00";
                _pollCts?.Cancel();
                var task = _pollTask;
                if (task != null) { try { task.Wait(200); } catch { } }
                _pollTask = null;
                StopUiFlushTimer();
            }
        }

        private void StartElapsedTimer()
        {
            StopElapsedTimer();
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += (_, _) =>
            {
                if (_connectedAt is { } start)
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

        [RelayCommand]
        private async Task Connect()
        {
            if (string.IsNullOrEmpty(SelectedPort))
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Serial.Msg_PortNotSelected"));
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
                await _serialPortService.OpenAsync(SelectedPort, baudRate, parity, dataBits, stopBits, cts.Token);
                IsConnected = _serialPortService.IsOpen;
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
                _notificationService.ShowWarn(timeoutMsg);
            }
            catch (Exception ex)
            {
                ConnectionStatus = ConnectionStatus.Error;
                IsConnected = false;
                var errMsg = LocalizationRegistry.Get("Serial.Status_ConnectFail", ex.Message);
                Log.Append(LogKind.Error, errMsg);
                _notificationService.ShowError(errMsg);
            }
        }

        [RelayCommand]
        private void Disconnect()
        {
            _pollCts?.Cancel();
            _serialPortService.Close();
            IsConnected = _serialPortService.IsOpen;
            IsPolling = false;
        }

        [RelayCommand]
        private async Task Send()
        {
            if (string.IsNullOrEmpty(SendText)) return;
            string encoding = Encodings[EncodingIndex];
            try
            {
                await _serialPortService.SendAsync(SendText, IsHexSend, encoding).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var errMsg = LocalizationRegistry.Get("Serial.Status_SendFail", ex.Message);
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
        private void RefreshPorts()
        {
            PortNames.Clear();
            foreach (var port in _serialPortService.GetPortNames())
            {
                PortNames.Add(port);
            }
            if (PortNames.Count > 0 && string.IsNullOrEmpty(SelectedPort))
            {
                SelectedPort = PortNames[0];
            }
        }

        [RelayCommand]
        private void ToggleRts()
        {
            _notificationService.ShowInfo($"RTS: {LocalizationRegistry.Get(IsRtsEnabled ? "Serial.Label_Enabled" : "Serial.Label_Disabled")}");
        }

        [RelayCommand]
        private void ToggleDtr()
        {
            _notificationService.ShowInfo($"DTR: {LocalizationRegistry.Get(IsDtrEnabled ? "Serial.Label_Enabled" : "Serial.Label_Disabled")}");
        }

        [RelayCommand]
        private async Task SaveData()
        {
            await LogFileHelper.SaveAsync(Log, _fileDialogService, _notificationService, "Serial");
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
            string encoding = Encodings[EncodingIndex];
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
                        if (!IsConnected) break;
                        try
                        {
                            await _serialPortService.SendAsync(SendText, IsHexSend, encoding).ConfigureAwait(false);
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

        private void OnFrameBreakIntervalChanged(int value)
        {
            _serialPortService.SetFrameBreakInterval(value);
        }

        private void OnEncodingIndexChanged(int value)
        {
            if (value == 0)
            {
                IsHexDisplay = false;
            }
        }
    }
}
