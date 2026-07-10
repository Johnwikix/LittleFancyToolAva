using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels
{
    public partial class ModbusPollViewModel : ViewModelBase
    {
        private readonly IModbusPollService _pollService;
        private readonly INotificationService _notificationService;
        private CancellationTokenSource? _pollCts;
        private readonly Dictionary<ushort, PollTableRow> _rowMap = [];

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
        private int _functionCodeIndex = 2;

        [ObservableProperty]
        private string _slaveId = "1";

        [ObservableProperty]
        private string _startAddress = "0";

        [ObservableProperty]
        private string _quantity = "10";

        [ObservableProperty]
        private string _scanTime = "1000";

        [ObservableProperty]
        private string _statusText = "就绪";

        [ObservableProperty]
        private string _logText = string.Empty;

        [ObservableProperty]
        private bool _isConnected;

        [ObservableProperty]
        private bool _isPolling;

        [ObservableProperty]
        private int _txCount;

        [ObservableProperty]
        private int _errorCount;

        [ObservableProperty]
        private string _writeAddress = "0";

        [ObservableProperty]
        private string _writeValue = "0";

        public ModbusPollViewModel(IModbusPollService pollService, INotificationService notificationService)
        {
            _pollService = pollService;
            _notificationService = notificationService;
            _pollService.LogReceived += OnLogReceived;
            _pollService.StatusChanged += OnStatusChanged;
            _pollService.ValueRefreshed += OnValueRefreshed;
            _pollService.StatsUpdated += OnStatsUpdated;
            RefreshPorts();
        }

        private void OnLogReceived(string log)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LogText += $"[{DateTime.Now:HH:mm:ss}] {log}\n";
            });
        }

        private void OnStatusChanged(string status)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StatusText = status;
            });
        }

        private void OnValueRefreshed(ushort address, ushort value, byte functionCode)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
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
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                TxCount = _pollService.TxCount;
                ErrorCount = _pollService.ErrorCount;
            });
        }

        [RelayCommand]
        private async Task Connect()
        {
            if (string.IsNullOrEmpty(SelectedPort))
            {
                _notificationService.ShowWarn("请选择串口");
                return;
            }

            try
            {
                int baudRate = int.Parse(BaudRates[BaudRateIndex]);
                Parity parity = (Parity)ParityIndex;
                int dataBits = int.Parse(DataBitsList[DataBitsIndex]);
                int stopBitsIndex = StopBitsIndex;
                StopBits stopBits = stopBitsIndex switch
                {
                    0 => StopBits.One,
                    1 => StopBits.OnePointFive,
                    2 => StopBits.Two,
                    _ => StopBits.One
                };

                await _pollService.ConnectAsync(SelectedPort, baudRate, parity, dataBits, stopBits);
                IsConnected = _pollService.IsConnected;
                BuildTableRows();
            }
            catch (Exception ex)
            {
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
        }

        [RelayCommand]
        private async Task StartPoll()
        {
            if (!IsConnected)
            {
                _notificationService.ShowWarn("请先连接");
                return;
            }

            if (!byte.TryParse(SlaveId, out byte slaveId) ||
                !ushort.TryParse(StartAddress, out ushort startAddr) ||
                !ushort.TryParse(Quantity, out ushort qty) ||
                !int.TryParse(ScanTime, out int scanMs))
            {
                _notificationService.ShowWarn("参数格式错误");
                return;
            }

            IsPolling = true;
            _pollCts = new CancellationTokenSource();
            byte functionCode = (byte)(FunctionCodeIndex + 1);

            BuildTableRows();

            try
            {
                await _pollService.StartPollingAsync(slaveId, functionCode, startAddr, qty, Math.Max(50, scanMs), _pollCts.Token);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
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
        }

        [RelayCommand]
        private async Task WriteCoil()
        {
            if (!IsConnected) return;
            if (!ushort.TryParse(WriteAddress, out ushort addr) ||
                !ushort.TryParse(WriteValue, out ushort val))
            {
                _notificationService.ShowWarn("地址或值格式错误");
                return;
            }

            if (!byte.TryParse(SlaveId, out byte slaveId)) return;
            await _pollService.WriteSingleCoilAsync(slaveId, addr, val != 0);
        }

        [RelayCommand]
        private async Task WriteRegister()
        {
            if (!IsConnected) return;
            if (!ushort.TryParse(WriteAddress, out ushort addr) ||
                !ushort.TryParse(WriteValue, out ushort val))
            {
                _notificationService.ShowWarn("地址或值格式错误");
                return;
            }

            if (!byte.TryParse(SlaveId, out byte slaveId)) return;
            await _pollService.WriteSingleRegisterAsync(slaveId, addr, val);
        }

        [RelayCommand]
        private void ClearLog()
        {
            LogText = string.Empty;
        }

        [RelayCommand]
        private void RefreshPorts()
        {
            PortNames.Clear();
            foreach (var port in System.IO.Ports.SerialPort.GetPortNames())
            {
                PortNames.Add(port);
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
