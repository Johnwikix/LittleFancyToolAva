using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels
{
    public partial class TcpServerViewModel : ViewModelBase
    {
        private readonly ITcpServerService _tcpService;
        private readonly INotificationService _notificationService;
        private CancellationTokenSource? _cts;

        public ObservableCollection<string> Modes { get; } = ["服务器模式", "客户端模式"];
        public ObservableCollection<string> ConnectedClients => _tcpService.ConnectedClients;

        [ObservableProperty]
        private int _modeIndex;

        [ObservableProperty]
        private string _address = "127.0.0.1";

        [ObservableProperty]
        private string _port = "8080";

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
        private string _selectedClient = string.Empty;

        [ObservableProperty]
        private bool _isRunning;

        [ObservableProperty]
        private bool _isConnected;

        public TcpServerViewModel(ITcpServerService tcpService, INotificationService notificationService)
        {
            _tcpService = tcpService;
            _notificationService = notificationService;
            _tcpService.DataReceived += OnDataReceived;
            _tcpService.StatusChanged += OnStatusChanged;
        }

        private void OnDataReceived(string data)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ReceivedText += data + "\n";
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
            if (!int.TryParse(Port, out int portNum) || portNum < 1 || portNum > 65535)
            {
                _notificationService.ShowWarn("端口号无效 (1-65535)");
                return;
            }

            try
            {
                _cts = new CancellationTokenSource();

                if (ModeIndex == 0)
                {
                    await _tcpService.StartServerAsync(Address, portNum, _cts.Token);
                    IsRunning = _tcpService.IsRunning;
                }
                else
                {
                    await _tcpService.ConnectClientAsync(Address, portNum, _cts.Token);
                    IsConnected = true;
                }
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"启动失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private void Stop()
        {
            _cts?.Cancel();
            if (ModeIndex == 0)
            {
                _tcpService.StopServer();
                IsRunning = false;
            }
            else
            {
                _tcpService.DisconnectClient();
                IsConnected = false;
            }
        }

        [RelayCommand]
        private async Task Send()
        {
            if (string.IsNullOrEmpty(SendText)) return;

            if (ModeIndex == 0 && !IsRunning)
            {
                _notificationService.ShowWarn("服务器未运行");
                return;
            }
            if (ModeIndex != 0 && !IsConnected)
            {
                _notificationService.ShowWarn("未连接");
                return;
            }

            string? target = ModeIndex == 0 ? SelectedClient : null;
            if (ModeIndex == 0 && string.IsNullOrEmpty(target) && ConnectedClients.Count > 0)
            {
                target = null;
            }

            await _tcpService.SendAsync(SendText, IsHexSend, target);
        }

        [RelayCommand]
        private void Clear()
        {
            ReceivedText = string.Empty;
        }

        partial void OnModeIndexChanged(int value)
        {
            if (value == 0)
            {
                _tcpService.DisconnectClient();
                IsConnected = false;
                StatusText = "就绪 (服务器模式)";
            }
            else
            {
                _tcpService.StopServer();
                IsRunning = false;
                StatusText = "就绪 (客户端模式)";
            }
        }
    }
}
