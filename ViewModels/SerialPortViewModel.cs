using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.Utils;

namespace LittleFancyToolAva.ViewModels
{
    public partial class SerialPortViewModel : ViewModelBase
    {
        private readonly ISerialPortService _serialPortService;
        private readonly INotificationService _notificationService;
        private CancellationTokenSource? _pollCts;

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

        [ObservableProperty]
        private string _selectedPort = string.Empty;

        [ObservableProperty]
        private int _baudRateIndex;

        [ObservableProperty]
        private int _parityIndex;

        [ObservableProperty]
        private int _dataBitsIndex = 3;

        [ObservableProperty]
        private int _stopBitsIndex;

        [ObservableProperty]
        private int _encodingIndex;

        [ObservableProperty]
        private string _receivedText = string.Empty;

        [ObservableProperty]
        private string _sendText = string.Empty;

        [ObservableProperty]
        private string _statusText = "就绪";

        [ObservableProperty]
        private bool _isHexSend;

        [ObservableProperty]
        private bool _isHexDisplay;

        [ObservableProperty]
        private bool _isPolling;

        [ObservableProperty]
        private int _pollInterval = 1000;

        [ObservableProperty]
        private int _frameBreakInterval = 20;

        [ObservableProperty]
        private bool _isRtsEnabled;

        [ObservableProperty]
        private bool _isDtrEnabled;

        [ObservableProperty]
        private bool _isConnected;

        public SerialPortViewModel(ISerialPortService serialPortService, INotificationService notificationService)
        {
            _serialPortService = serialPortService;
            _notificationService = notificationService;
            _serialPortService.DataReceived += OnDataReceived;
            _serialPortService.StatusChanged += OnStatusChanged;
            RefreshPorts();
        }

        private void OnDataReceived(string data)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (IsHexDisplay)
                {
                    byte[] bytes = ToolMethod.GetEncodedData(data, ToolMethod.EncodingMode.UTF8);
                    ReceivedText += ToolMethod.ByteArrayToHexString(bytes) + "\n";
                }
                else
                {
                    ReceivedText += data;
                }
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

                await _serialPortService.OpenAsync(SelectedPort, baudRate, parity, dataBits, stopBits);
                IsConnected = _serialPortService.IsOpen;
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
            _serialPortService.Close();
            IsConnected = _serialPortService.IsOpen;
            IsPolling = false;
        }

        [RelayCommand]
        private async Task Send()
        {
            if (string.IsNullOrEmpty(SendText)) return;
            string encoding = Encodings[EncodingIndex];
            await _serialPortService.SendAsync(SendText, IsHexSend, encoding);
        }

        [RelayCommand]
        private void Clear()
        {
            ReceivedText = string.Empty;
        }

        [RelayCommand]
        private void RefreshPorts()
        {
            PortNames.Clear();
            foreach (var port in _serialPortService.GetPortNames())
            {
                PortNames.Add(port);
            }
        }

        [RelayCommand]
        private void ToggleRts()
        {
            _notificationService.ShowInfo($"RTS: {(IsRtsEnabled ? "启用" : "禁用")}");
        }

        [RelayCommand]
        private void ToggleDtr()
        {
            _notificationService.ShowInfo($"DTR: {(IsDtrEnabled ? "启用" : "禁用")}");
        }

        [RelayCommand]
        private async Task SaveData()
        {
            if (string.IsNullOrEmpty(ReceivedText))
            {
                _notificationService.ShowWarn("没有可保存的数据");
                return;
            }

            var app = App.Current as App;
            var dialog = app?.TryGetService<Services.IFileDialogService>();
            if (dialog == null) return;

            string? path = await dialog.PickSaveFileAsync("保存接收数据", "received.txt");
            if (path != null)
            {
                await File.WriteAllTextAsync(path, ReceivedText);
                _notificationService.ShowSuccess("数据已保存");
            }
        }

        [RelayCommand]
        private async Task StartPolling()
        {
            if (!IsConnected)
            {
                _notificationService.ShowWarn("请先连接串口");
                return;
            }

            IsPolling = true;
            _pollCts = new CancellationTokenSource();
            string encoding = Encodings[EncodingIndex];
            try
            {
                await _serialPortService.SendWithIntervalAsync(SendText, IsHexSend, encoding, PollInterval, _pollCts.Token);
            }
            catch (TaskCanceledException) { }
            finally
            {
                IsPolling = false;
            }
        }

        [RelayCommand]
        private void StopPolling()
        {
            _pollCts?.Cancel();
            IsPolling = false;
        }

        partial void OnEncodingIndexChanged(int value)
        {
            if (value == 0)
            {
                IsHexDisplay = false;
            }
        }
    }
}
