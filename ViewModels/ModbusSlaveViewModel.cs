using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels
{
    public partial class ModbusSlaveViewModel : ViewModelBase
    {
        private readonly IModbusSlaveService _slaveService;
        private readonly INotificationService _notificationService;

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
        private string _logText = string.Empty;

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

        public ModbusSlaveViewModel(IModbusSlaveService slaveService, INotificationService notificationService)
        {
            _slaveService = slaveService;
            _notificationService = notificationService;
            _slaveService.LogReceived += OnLogReceived;
            _slaveService.StatusChanged += OnStatusChanged;
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

        [RelayCommand]
        private async Task Start()
        {
            if (string.IsNullOrEmpty(SelectedPort))
            {
                _notificationService.ShowWarn("请选择串口");
                return;
            }

            if (!byte.TryParse(SlaveId, out byte slaveId) ||
                !ushort.TryParse(CoilCount, out ushort coilCnt) ||
                !ushort.TryParse(RegisterCount, out ushort regCnt))
            {
                _notificationService.ShowWarn("参数格式错误");
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

                await _slaveService.StartAsync(SelectedPort, baudRate, parity, dataBits, stopBits, slaveId, coilCnt, regCnt);
                IsRunning = _slaveService.IsRunning;
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"启动失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private void Stop()
        {
            _slaveService.Stop();
            IsRunning = _slaveService.IsRunning;
        }

        [RelayCommand]
        private async Task UpdateCoil()
        {
            if (!int.TryParse(SelectedCoilIndex, out int index))
            {
                _notificationService.ShowWarn("索引格式错误");
                return;
            }
            await _slaveService.WriteCoilAsync(index, SelectedCoilValue);
        }

        [RelayCommand]
        private async Task UpdateRegister()
        {
            if (!int.TryParse(SelectedRegisterIndex, out int index) ||
                !ushort.TryParse(SelectedRegisterValue, out ushort value))
            {
                _notificationService.ShowWarn("参数格式错误");
                return;
            }
            await _slaveService.WriteHoldingRegisterAsync(index, value);
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
    }
}
