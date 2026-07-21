using System.Collections.ObjectModel;
using System.IO.Ports;
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
    public partial class SerialPortViewModel : ViewModelBase, IDisposable, IViewState, IViewLifecycle
    {
        private readonly ISerialPortService _serialPortService;
        private readonly INotificationService _notificationService;
        private readonly IViewStateService _viewStateService;
        private CancellationTokenSource? _pollCts;
        private DispatcherTimer? _elapsedTimer;
        private DateTime? _connectedAt;
        private bool _disposed;

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

        public SerialPortViewModel(ISerialPortService serialPortService, INotificationService notificationService, IViewStateService viewStateService)
        {
            _serialPortService = serialPortService;
            _notificationService = notificationService;
            _viewStateService = viewStateService;
            RefreshPorts();
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
            _serialPortService.BytesReceived += OnBytesReceived;
            _serialPortService.StatusChanged += OnStatusChanged;
            if (IsConnected)
            {
                StartElapsedTimer();
            }
        }

        void IViewLifecycle.OnNavigatedFrom()
        {
            _serialPortService.BytesReceived -= OnBytesReceived;
            _serialPortService.StatusChanged -= OnStatusChanged;
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
            StopElapsedTimer();
        }

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
            Dispatcher.UIThread.Post(() =>
            {
                if (IsHexDisplay)
                {
                    Log.Append(LogKind.Rx, ToolMethod.ByteArrayToHexString(bytes));
                }
                else
                {
                    Log.AppendLine(LogKind.Rx, DecodeBytes(bytes));
                }
                RxCount++;
            });
        }

        private string DecodeBytes(byte[] bytes)
        {
            string encoding = Encodings[EncodingIndex];
            try
            {
                return ToolMethod.GetEncoding(ParseEncodingMode(encoding)).GetString(bytes);
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

        private void OnIsConnectedChanged(bool value)
        {
            if (value)
            {
                ConnectionStatus = ConnectionStatus.Connected;
                StatusDetail = $"{SelectedPort} @ {BaudRates[BaudRateIndex]}";
                _connectedAt = DateTime.Now;
                StartElapsedTimer();
            }
            else
            {
                ConnectionStatus = ConnectionStatus.Idle;
                StatusDetail = string.Empty;
                StopElapsedTimer();
                _connectedAt = null;
                ElapsedText = "00:00:00";
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

        [RelayCommand]
        private async Task Connect()
        {
            if (string.IsNullOrEmpty(SelectedPort))
            {
                _notificationService.ShowWarn("未选择串口，请先在端口设置中选择串口。");
                return;
            }

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
                await _serialPortService.OpenAsync(SelectedPort, baudRate, parity, dataBits, stopBits);
                IsConnected = _serialPortService.IsOpen;
                if (IsConnected)
                {
                    Log.Append(LogKind.System, $"已连接到 {SelectedPort} @ {baudRate}");
                }
            }
            catch (Exception ex)
            {
                ConnectionStatus = ConnectionStatus.Error;
                Log.Append(LogKind.Error, $"连接失败: {ex.Message}");
                _notificationService.ShowError($"连接失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private void Disconnect()
        {
            _pollCts?.Cancel();
            _serialPortService.Close();
            IsConnected = _serialPortService.IsOpen;
            IsPolling = false;
            Log.Append(LogKind.System, "已断开连接");
        }

        [RelayCommand]
        private async Task Send()
        {
            if (string.IsNullOrEmpty(SendText)) return;
            string encoding = Encodings[EncodingIndex];
            try
            {
                await _serialPortService.SendAsync(SendText, IsHexSend, encoding);
                AppendTxLog(SendText, encoding);
                TxCount++;
            }
            catch (Exception ex)
            {
                Log.Append(LogKind.Error, $"发送失败: {ex.Message}");
                _notificationService.ShowError($"发送失败: {ex.Message}");
            }
        }

        private void AppendTxLog(string text, string encoding)
        {
            if (IsHexDisplay)
            {
                byte[] bytes = IsHexSend
                    ? ToolMethod.HexStringToBytes(text)
                    : ToolMethod.GetEncodedData(text, ParseEncodingMode(encoding));
                Log.Append(LogKind.Tx, ToolMethod.ByteArrayToHexString(bytes));
            }
            else
            {
                Log.Append(LogKind.Tx, text);
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
            _notificationService.ShowInfo($"RTS: {(IsRtsEnabled ? "已启用" : "已禁用")}");
        }

        [RelayCommand]
        private void ToggleDtr()
        {
            _notificationService.ShowInfo($"DTR: {(IsDtrEnabled ? "已启用" : "已禁用")}");
        }

        [RelayCommand]
        private async Task SaveData()
        {
            if (Log.Count == 0)
            {
                _notificationService.ShowWarn("没有可保存的接收数据。");
                return;
            }

            var app = App.Current as App;
            var dialog = app?.TryGetService<Services.IFileDialogService>();
            if (dialog == null) return;

            string? path = await dialog.PickSaveFileAsync("保存接收数据", "received.txt");
            if (path != null)
            {
                var lines = Log.Entries.Select(e => $"{e.TimestampText} {e.Tag}: {e.Text}");
                await File.WriteAllLinesAsync(path, lines);
                _notificationService.ShowSuccess($"数据已保存到 {path}");
            }
        }

        [RelayCommand]
        private async Task StartPolling()
        {
            if (!IsConnected)
            {
                _notificationService.ShowWarn("请先连接串口。");
                return;
            }
            if (IsPolling) return;

            var localCts = new CancellationTokenSource();
            _pollCts = localCts;
            IsPolling = true;
            string encoding = Encodings[EncodingIndex];
            int sentCount = 0;
            try
            {
                while (!localCts.Token.IsCancellationRequested)
                {
                    if (!IsConnected) break;
                    await _serialPortService.SendAsync(SendText, IsHexSend, encoding);
                    AppendTxLog(SendText, encoding);
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