using System.Collections.ObjectModel;
using System.IO.Ports;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.Utils;

namespace LittleFancyToolAva.ViewModels
{
    public partial class ModbusPollViewModel : ViewModelBase, IDisposable, IViewState, IViewLifecycle
    {
        private readonly IModbusPollService _pollService;
        private readonly INotificationService _notificationService;
        private readonly IViewStateService _viewStateService;
        private CancellationTokenSource? _pollCts;
        private DispatcherTimer? _elapsedTimer;
        private DateTime? _startedAt;
        private readonly Dictionary<ushort, PollTableRow> _rowMap = [];
        private bool _disposed;

        public ObservableCollection<string> PortNames { get; } = [];
        public ObservableCollection<string> BaudRates { get; } =
            ["9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600"];
        public ObservableCollection<string> Parities { get; } =
            ["None", "Odd", "Even", "Mark", "Space"];
        public ObservableCollection<string> DataBitsList { get; } =
            ["5", "6", "7", "8"];
        public ObservableCollection<string> StopBitsList { get; } =
            ["1", "1.5", "2"];
        public ObservableCollection<string> FunctionCodes { get; } =
            ["01 读线圈", "02 读离散输入", "03 读保持寄存器", "04 读输入寄存器"];

        public ObservableCollection<PollTableRow> PollRows { get; } = [];
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
        } = 4;

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

        public int FunctionCodeIndex
        {
            get;
            set => SetProperty(ref field, value);
        } = 2;

        public string SlaveId
        {
            get;
            set => SetProperty(ref field, value);
        } = "1";

        public string StartAddress
        {
            get;
            set => SetProperty(ref field, value);
        } = "0";

        public string Quantity
        {
            get;
            set => SetProperty(ref field, value);
        } = "10";

        public string ScanTime
        {
            get;
            set => SetProperty(ref field, value);
        } = "1000";

        public string StatusText
        {
            get;
            set => SetProperty(ref field, value);
        } = "就绪";

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

        public bool IsPolling
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnIsPollingChanged(value);
                }
            }
        }

        public int TxCount
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int ErrorCount
        {
            get;
            set => SetProperty(ref field, value);
        }

        public string WriteAddress
        {
            get;
            set => SetProperty(ref field, value);
        } = "0";

        public string WriteValue
        {
            get;
            set => SetProperty(ref field, value);
        } = "0";

        public ConnectionStatus ConnectionStatus
        {
            get;
            set => SetProperty(ref field, value);
        } = ConnectionStatus.Idle;

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

        string IViewState.ViewName => "modbusPollView";

        public ModbusPollViewModel(IModbusPollService pollService, INotificationService notificationService, IViewStateService viewStateService)
        {
            _pollService = pollService;
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
            _pollService.LogReceived += OnLogReceived;
            _pollService.StatusChanged += OnStatusChanged;
            _pollService.ValueRefreshed += OnValueRefreshed;
            _pollService.StatsUpdated += OnStatsUpdated;
            if (IsConnected)
            {
                StartElapsedTimer();
            }
        }

        void IViewLifecycle.OnNavigatedFrom()
        {
            _pollService.LogReceived -= OnLogReceived;
            _pollService.StatusChanged -= OnStatusChanged;
            _pollService.ValueRefreshed -= OnValueRefreshed;
            _pollService.StatsUpdated -= OnStatsUpdated;
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
            StopElapsedTimer();
        }

        object IViewState.CaptureState() => new ModbusPollViewState
        {
            SelectedPort = SelectedPort,
            BaudRateIndex = BaudRateIndex,
            ParityIndex = ParityIndex,
            DataBitsIndex = DataBitsIndex,
            StopBitsIndex = StopBitsIndex,
            FunctionCodeIndex = FunctionCodeIndex,
            SlaveId = SlaveId,
            StartAddress = StartAddress,
            Quantity = Quantity,
            ScanTime = ScanTime,
            WriteAddress = WriteAddress,
            WriteValue = WriteValue
        };

        void IViewState.RestoreState(object state)
        {
            if (state is ModbusPollViewState s)
            {
                SelectedPort = s.SelectedPort;
                BaudRateIndex = s.BaudRateIndex;
                ParityIndex = s.ParityIndex;
                DataBitsIndex = s.DataBitsIndex;
                StopBitsIndex = s.StopBitsIndex;
                FunctionCodeIndex = s.FunctionCodeIndex;
                SlaveId = s.SlaveId;
                StartAddress = s.StartAddress;
                Quantity = s.Quantity;
                ScanTime = s.ScanTime;
                WriteAddress = s.WriteAddress;
                WriteValue = s.WriteValue;
            }
        }

        private void OnLogReceived(string log)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Log.AppendLine(LogKind.System, log);
            });
        }

        private void OnStatusChanged(string status)
        {
            Dispatcher.UIThread.Post(() => StatusText = status);
        }

        private void OnValueRefreshed(ushort address, ushort value, byte functionCode)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_rowMap.TryGetValue(address, out var row))
                {
                    row.ValueDec = value.ToString();
                    row.ValueHex = $"0x{value:X4}";
                    row.LastUpdate = DateTime.Now.ToString("HH:mm:ss");
                }
            });
        }

        private void OnStatsUpdated()
        {
            Dispatcher.UIThread.Post(() =>
            {
                TxCount = _pollService.TxCount;
                ErrorCount = _pollService.ErrorCount;
            });
        }

        private void OnIsConnectedChanged(bool value)
        {
            if (value)
            {
                ConnectionStatus = ConnectionStatus.Connected;
                StatusDetail = $"{SelectedPort} @ {BaudRates[BaudRateIndex]}";
            }
            else
            {
                ConnectionStatus = ConnectionStatus.Idle;
                StatusDetail = string.Empty;
                StopElapsedTimer();
            }
        }

        private void OnIsPollingChanged(bool value)
        {
            if (value) StartElapsedTimer();
            else StopElapsedTimer();
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
            if (!IsPolling)
            {
                _elapsedTimer?.Stop();
                _elapsedTimer = null;
                _startedAt = null;
                ElapsedText = "00:00:00";
            }
        }

        [RelayCommand]
        private async Task Connect()
        {
            if (string.IsNullOrEmpty(SelectedPort))
            {
                _notificationService.ShowWarn("请先选择串口。");
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
                await _pollService.ConnectAsync(SelectedPort, baudRate, parity, dataBits, stopBits);
                IsConnected = _pollService.IsConnected;
                if (IsConnected)
                {
                    BuildTableRows();
                    Log.Append(LogKind.System, $"已连接 {SelectedPort} @ {baudRate}");
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
            _pollService.Disconnect();
            IsConnected = _pollService.IsConnected;
            IsPolling = false;
            Log.Append(LogKind.System, "已断开连接");
        }

        [RelayCommand]
        private async Task StartPoll()
        {
            if (!IsConnected)
            {
                _notificationService.ShowWarn("请先连接串口。");
                return;
            }

            if (!byte.TryParse(SlaveId, out byte slaveId) ||
                !ushort.TryParse(StartAddress, out ushort startAddr) ||
                !ushort.TryParse(Quantity, out ushort qty) ||
                !int.TryParse(ScanTime, out int scanMs))
            {
                _notificationService.ShowWarn("请检查从站 ID / 地址 / 数量 / 扫描时间。");
                return;
            }

            IsPolling = true;
            _pollCts = new CancellationTokenSource();
            byte functionCode = (byte)(FunctionCodeIndex + 1);

            BuildTableRows();
            Log.Append(LogKind.System, $"开始轮询 FC{functionCode:D2} 从站 {slaveId} 地址 {startAddr} 数量 {qty}");

            try
            {
                await _pollService.StartPollingAsync(slaveId, functionCode, startAddr, qty, Math.Max(50, scanMs), _pollCts.Token);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Log.Append(LogKind.Error, $"轮询错误: {ex.Message}");
                _notificationService.ShowError($"轮询错误: {ex.Message}");
            }
            finally
            {
                IsPolling = false;
            }
        }

        [RelayCommand]
        private void StopPoll()
        {
            _pollCts?.Cancel();
            IsPolling = false;
            Log.Append(LogKind.System, "停止轮询");
        }

        [RelayCommand]
        private async Task WriteCoil()
        {
            if (!IsConnected) return;
            if (!ushort.TryParse(WriteAddress, out ushort addr) ||
                !ushort.TryParse(WriteValue, out ushort val))
            {
                _notificationService.ShowWarn("地址或值格式错误。");
                return;
            }
            if (!byte.TryParse(SlaveId, out byte slaveId)) return;
            try
            {
                await _pollService.WriteSingleCoilAsync(slaveId, addr, val != 0);
                Log.Append(LogKind.Tx, $"写线圈 FC05 从站 {slaveId} 地址 {addr} = {val}");
            }
            catch (Exception ex)
            {
                Log.Append(LogKind.Error, $"写线圈失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task WriteRegister()
        {
            if (!IsConnected) return;
            if (!ushort.TryParse(WriteAddress, out ushort addr) ||
                !ushort.TryParse(WriteValue, out ushort val))
            {
                _notificationService.ShowWarn("地址或值格式错误。");
                return;
            }
            if (!byte.TryParse(SlaveId, out byte slaveId)) return;
            try
            {
                await _pollService.WriteSingleRegisterAsync(slaveId, addr, val);
                Log.Append(LogKind.Tx, $"写寄存器 FC06 从站 {slaveId} 地址 {addr} = {val}");
            }
            catch (Exception ex)
            {
                Log.Append(LogKind.Error, $"写寄存器失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ClearLog()
        {
            Log.Clear();
            TxCount = 0;
            ErrorCount = 0;
        }

        [RelayCommand]
        private void RefreshPorts()
        {
            PortNames.Clear();
            foreach (var port in System.IO.Ports.SerialPort.GetPortNames())
            {
                PortNames.Add(port);
            }
            if (PortNames.Count > 0 && string.IsNullOrEmpty(SelectedPort))
            {
                SelectedPort = PortNames[0];
            }
        }

        private void BuildTableRows()
        {
            PollRows.Clear();
            _rowMap.Clear();

            if (!ushort.TryParse(Quantity, out ushort qty) ||
                !ushort.TryParse(StartAddress, out ushort startAddr))
                return;

            for (ushort i = 0; i < qty; i++)
            {
                ushort addr = (ushort)(startAddr + i);
                var row = new PollTableRow
                {
                    Address = addr.ToString(),
                    ValueDec = "0",
                    ValueHex = "0x0000",
                    LastUpdate = "-"
                };
                PollRows.Add(row);
                _rowMap[addr] = row;
            }
        }
    }
}