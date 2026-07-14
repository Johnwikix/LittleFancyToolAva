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
    public partial class ModbusSlaveViewModel : ViewModelBase, IDisposable, IViewState, IViewLifecycle
    {
        private readonly IModbusSlaveService _slaveService;
        private readonly INotificationService _notificationService;
        private readonly IViewStateService _viewStateService;
        private DispatcherTimer? _elapsedTimer;
        private DateTime? _startedAt;
        private int _requestCount;
        private int _errorCount;
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

        public ObservableCollection<SlaveTableRow> Coils => _slaveService.Coils;
        public ObservableCollection<SlaveTableRow> HoldingRegisters => _slaveService.HoldingRegisters;
        public LogBuffer Log { get; } = new();

        [ObservableProperty]
        private string _selectedPort = string.Empty;

        [ObservableProperty]
        private int _baudRateIndex = 4;

        [ObservableProperty]
        private int _parityIndex;

        [ObservableProperty]
        private int _dataBitsIndex = 3;

        [ObservableProperty]
        private int _stopBitsIndex;

        [ObservableProperty]
        private string _slaveId = "1";

        [ObservableProperty]
        private string _coilCount = "16";

        [ObservableProperty]
        private string _registerCount = "16";

        [ObservableProperty]
        private string _statusText = "就绪";

        [ObservableProperty]
        private bool _isRunning;

        [ObservableProperty]
        private string _selectedCoilIndex = "0";

        [ObservableProperty]
        private bool _selectedCoilValue;

        [ObservableProperty]
        private string _selectedRegisterIndex = "0";

        [ObservableProperty]
        private string _selectedRegisterValue = "0";

        [ObservableProperty]
        private ConnectionStatus _connectionStatus = ConnectionStatus.Idle;

        [ObservableProperty]
        private string _elapsedText = "00:00:00";

        [ObservableProperty]
        private string _statusDetail = string.Empty;

        [ObservableProperty]
        private int _requestCountDisplay;

        [ObservableProperty]
        private int _errorCountDisplay;

        string IViewState.ViewName => "modbusSlaveView";

        public ModbusSlaveViewModel(IModbusSlaveService slaveService, INotificationService notificationService, IViewStateService viewStateService)
        {
            _slaveService = slaveService;
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
            _slaveService.LogReceived += OnLogReceived;
            _slaveService.StatusChanged += OnStatusChanged;
            if (IsRunning)
            {
                StartTimer();
            }
        }

        void IViewLifecycle.OnNavigatedFrom()
        {
            _slaveService.LogReceived -= OnLogReceived;
            _slaveService.StatusChanged -= OnStatusChanged;
            StopTimer();
        }

        private void StartTimer()
        {
            _startedAt = DateTime.Now;
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += (_, _) =>
            {
                if (_startedAt is { } start)
                    ElapsedText = (DateTime.Now - start).ToString(@"hh\:mm\:ss");
            };
            _elapsedTimer.Start();
        }

        private void StopTimer()
        {
            _elapsedTimer?.Stop();
            _elapsedTimer = null;
            _startedAt = null;
            ElapsedText = "00:00:00";
        }

        object IViewState.CaptureState() => new ModbusSlaveViewState
        {
            SelectedPort = SelectedPort,
            BaudRateIndex = BaudRateIndex,
            ParityIndex = ParityIndex,
            DataBitsIndex = DataBitsIndex,
            StopBitsIndex = StopBitsIndex,
            SlaveId = SlaveId,
            CoilCount = CoilCount,
            RegisterCount = RegisterCount
        };

        void IViewState.RestoreState(object state)
        {
            if (state is ModbusSlaveViewState s)
            {
                SelectedPort = s.SelectedPort;
                BaudRateIndex = s.BaudRateIndex;
                ParityIndex = s.ParityIndex;
                DataBitsIndex = s.DataBitsIndex;
                StopBitsIndex = s.StopBitsIndex;
                SlaveId = s.SlaveId;
                CoilCount = s.CoilCount;
                RegisterCount = s.RegisterCount;
            }
        }

        private void OnLogReceived(string log)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Log.AppendLine(LogKind.System, log);
                if (log.Contains("错误") || log.Contains("失败")) _errorCount++;
                else _requestCount++;
                RequestCountDisplay = _requestCount;
                ErrorCountDisplay = _errorCount;
            });
        }

        private void OnStatusChanged(string status)
        {
            Dispatcher.UIThread.Post(() => StatusText = status);
        }

        partial void OnIsRunningChanged(bool value)
        {
            if (value)
            {
                ConnectionStatus = ConnectionStatus.Connected;
                StatusDetail = $"{SelectedPort} @ {BaudRates[BaudRateIndex]}";
                StartTimer();
            }
            else
            {
                ConnectionStatus = ConnectionStatus.Idle;
                StatusDetail = string.Empty;
                StopTimer();
            }
        }

        [RelayCommand]
        private async Task Start()
        {
            if (string.IsNullOrEmpty(SelectedPort))
            {
                _notificationService.ShowWarn("请先选择串口。");
                return;
            }

            if (!byte.TryParse(SlaveId, out byte slaveId) ||
                !ushort.TryParse(CoilCount, out ushort coilCnt) ||
                !ushort.TryParse(RegisterCount, out ushort regCnt))
            {
                _notificationService.ShowWarn("请检查从站 ID / 线圈数 / 寄存器数。");
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
                await _slaveService.StartAsync(SelectedPort, baudRate, parity, dataBits, stopBits, slaveId, coilCnt, regCnt);
                IsRunning = _slaveService.IsRunning;
                _requestCount = 0;
                _errorCount = 0;
                RequestCountDisplay = 0;
                ErrorCountDisplay = 0;
                Log.Append(LogKind.System, $"Modbus Slave 已启动 从站 {slaveId} 线圈 {coilCnt} 寄存器 {regCnt}");
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
            _slaveService.Stop();
            IsRunning = _slaveService.IsRunning;
            Log.Append(LogKind.System, "Modbus Slave 已停止");
        }

        [RelayCommand]
        private async Task UpdateCoil()
        {
            if (!IsRunning)
            {
                _notificationService.ShowWarn("请先启动 Slave。");
                return;
            }
            if (!int.TryParse(SelectedCoilIndex, out int index))
            {
                _notificationService.ShowWarn("请输入有效索引。");
                return;
            }
            try
            {
                await _slaveService.WriteCoilAsync(index, SelectedCoilValue);
                Log.Append(LogKind.Tx, $"写线圈 {index} = {SelectedCoilValue}");
            }
            catch (Exception ex)
            {
                Log.Append(LogKind.Error, $"写线圈失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task UpdateRegister()
        {
            if (!IsRunning)
            {
                _notificationService.ShowWarn("请先启动 Slave。");
                return;
            }
            if (!int.TryParse(SelectedRegisterIndex, out int index) ||
                !ushort.TryParse(SelectedRegisterValue, out ushort value))
            {
                _notificationService.ShowWarn("索引或值格式错误。");
                return;
            }
            try
            {
                await _slaveService.WriteHoldingRegisterAsync(index, value);
                Log.Append(LogKind.Tx, $"写寄存器 {index} = {value}");
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
            _requestCount = 0;
            _errorCount = 0;
            RequestCountDisplay = 0;
            ErrorCountDisplay = 0;
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
    }
}