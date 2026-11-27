using System.Net;
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
    /// UDP 会话：一路本地绑定（普通或组播）。模式、本地地址/端口与组播地址决定绑定了什么，运行中冻结；
    /// 远程地址/端口与组播端口只是发送目标，运行中也能改。日志、计数与定时发送都归这一路自己。
    /// </summary>
    public partial class UdpSessionViewModel : TerminalSessionViewModelBase
    {
        private readonly IUdpService _service;
        private readonly IFileDialogService _fileDialogService;
        private readonly AppObserveModel _app;
        private string _displayName = string.Empty;

        public UdpSessionViewModel(IUdpService service, INotificationService notificationService,
            IFileDialogService fileDialogService, AppObserveModel app) : base(notificationService)
        {
            _service = service;
            _fileDialogService = fileDialogService;
            _app = app;

            _service.BytesReceived += OnBytesReceived;
            _service.DataSent += OnDataSent;
            _service.StatusChanged += OnStatusChanged;
            _service.ConnectionStateChanged += OnConnectionStateChanged;

            RefreshDisplayName();
            StatusText = ReadyText;
        }

        /// <summary>页面注入的端点冲突检查：与其它运行中的会话互斥时返回 true。</summary>
        public Func<UdpSessionViewModel, BindEndpoint, bool>? EndpointConflictCheck { get; set; }

        public IUdpService Service => _service;

        public override bool IsActive => IsRunning;

        // 共享的下拉项模板只有名称一行，发送目标并入显示名；随输入更新并缓存，不在读取时拼接。
        public override string DisplayName => _displayName;

        private void RefreshDisplayName()
        {
            string local = $"{LocalAddress}:{LocalPort}";
            string name = IsMulticast
                ? $"{local} → {LocalizationRegistry.Get("Udp.Label_MulticastSummary", MulticastAddress, MulticastPort)}"
                : $"{local} → {RemoteAddress}:{RemotePort}";
            SetProperty(ref _displayName, name, nameof(DisplayName));
        }

        protected override string NotActiveMessageKey => "Udp.Msg_NotStarted";

        protected override string TimedSendErrorMessageKey => "Udp.Msg_TimedSendError";

        private string ReadyText => LocalizationRegistry.Get("Udp.Status_Ready");

        public int ModeIndex
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(IsMulticast));
                    RefreshDisplayName();
                }
            }
        }

        /// <summary>组播模式（<see cref="ModeIndex"/> = 1）；普通模式为 false。</summary>
        public bool IsMulticast => ModeIndex == 1;

        public string LocalAddress
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) RefreshDisplayName();
            }
        } = "0.0.0.0";

        public string LocalPort
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) RefreshDisplayName();
            }
        } = "8080";

        public string MulticastAddress
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) RefreshDisplayName();
            }
        } = "239.0.0.1";

        public string MulticastPort
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) RefreshDisplayName();
            }
        } = "8080";

        public string RemoteAddress
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) RefreshDisplayName();
            }
        } = "127.0.0.1";

        public string RemotePort
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) RefreshDisplayName();
            }
        } = "9090";

        public bool IsRunning
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) OnRunningChanged();
            }
        }

        public override void RefreshCulture()
        {
            base.RefreshCulture();
            RefreshDisplayName();
            if (IsRunning)
            {
                StatusDetail = DisplayName;
            }
            else
            {
                StatusText = ReadyText;
            }
        }

        public UdpSessionViewState ToState() => new()
        {
            Id = Id,
            ModeIndex = ModeIndex,
            LocalAddress = LocalAddress,
            LocalPort = LocalPort,
            MulticastAddress = MulticastAddress,
            MulticastPort = MulticastPort,
            RemoteAddress = RemoteAddress,
            RemotePort = RemotePort,
            SendText = SendText,
            IsHexSend = IsHexSend,
            IsHexDisplay = IsHexDisplay,
            PollInterval = PollInterval,
            EnableSendCount = EnableSendCount,
            SendCount = SendCount
        };

        public void ApplyState(UdpSessionViewState state)
        {
            RestoreId(state.Id);
            ModeIndex = state.ModeIndex == 1 ? 1 : 0;
            LocalAddress = state.LocalAddress;
            LocalPort = state.LocalPort;
            MulticastAddress = state.MulticastAddress;
            MulticastPort = state.MulticastPort;
            RemoteAddress = state.RemoteAddress;
            RemotePort = state.RemotePort;
            SendText = state.SendText;
            IsHexSend = state.IsHexSend;
            IsHexDisplay = state.IsHexDisplay;
            PollInterval = state.PollInterval <= 0 ? 1000 : state.PollInterval;
            EnableSendCount = state.EnableSendCount;
            SendCount = state.SendCount <= 0 ? 1 : state.SendCount;
        }

        protected override string DecodeBytes(byte[] bytes) => Encoding.UTF8.GetString(bytes);

        private void OnRunningChanged()
        {
            OnPropertyChanged(nameof(IsActive));
            if (IsRunning)
            {
                ConnectionStatus = ConnectionStatus.Connected;
                StatusDetail = DisplayName;
                StartElapsed();
                return;
            }

            ConnectionStatus = ConnectionStatus.Idle;
            StatusDetail = string.Empty;
            StopElapsed();
            CancelPolling();
        }

        /// <summary>组播发往组播地址与组播端口；普通模式发往远程地址与端口。</summary>
        private (string Address, int Port) ResolveTarget()
        {
            if (IsMulticast)
            {
                int port = int.TryParse(MulticastPort, out int multicast) ? multicast
                    : int.TryParse(LocalPort, out int local) ? local
                    : 0;
                return (MulticastAddress, port);
            }

            return (RemoteAddress, int.TryParse(RemotePort, out int remote) ? remote : 9090);
        }

        public override async Task SendManualAsync()
        {
            if (string.IsNullOrEmpty(SendText)) return;
            if (!IsActive)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get(NotActiveMessageKey));
                return;
            }

            var (address, port) = ResolveTarget();
            try
            {
                await _service.SendAsync(SendText, IsHexSend, address, port).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var errMsg = LocalizationRegistry.Get("Udp.Msg_SendFail", ex.Message);
                Log.Enqueue(LogKind.Error, errMsg);
                RaiseFault(errMsg);
            }
        }

        protected override Func<Task> CreatePollingSender()
        {
            // 目标在开始时确定：之后修改远程或组播端口不影响进行中的定时发送，要换目标就停止后重新开始。
            var (address, port) = ResolveTarget();
            return () => _service.SendAsync(SendText, IsHexSend, address, port);
        }

        private void OnBytesReceived(byte[] bytes) => OnReceived(bytes);

        private void OnDataSent(byte[] bytes) => OnSent(bytes);

        private void OnStatusChanged(string status) => PostStatusText(status);

        private void OnConnectionStateChanged(object? sender, ConnectionEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (e.Type)
                {
                    case ConnectionEventType.Connected:
                        IsRunning = true;
                        Log.Append(LogKind.System, e.Message);
                        break;
                    case ConnectionEventType.Disconnected:
                        IsRunning = false;
                        IsPolling = false;
                        CancelPolling();
                        Log.Append(LogKind.System, e.Message);
                        break;
                    case ConnectionEventType.Lost:
                        IsRunning = false;
                        IsPolling = false;
                        CancelPolling();
                        Log.Append(LogKind.Error, e.Message);
                        break;
                    case ConnectionEventType.Error:
                        ConnectionStatus = ConnectionStatus.Error;
                        Log.Append(LogKind.Error, e.Message);
                        break;
                }
            });
        }

        [RelayCommand]
        private async Task Start()
        {
            if (!int.TryParse(LocalPort, out int localPort) || localPort < 1 || localPort > 65535)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Udp.Msg_InvalidLocalPort"));
                return;
            }

            string? multicastAddress = IsMulticast ? MulticastAddress : null;
            int? multicastPort = IsMulticast && int.TryParse(MulticastPort, out int multicast) ? multicast : null;
            if (IsMulticast && !string.IsNullOrEmpty(multicastAddress) && !IPAddress.TryParse(multicastAddress, out _))
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Udp.Msg_InvalidMulticast"));
                return;
            }

            var endpoint = BindEndpoint.ForUdp(LocalAddress, localPort);
            if (EndpointConflictCheck?.Invoke(this, endpoint) == true)
            {
                var conflict = LocalizationRegistry.Get("Udp.Msg_EndpointInUse", endpoint.ToString());
                Log.Append(LogKind.Error, conflict);
                ConnectionStatus = ConnectionStatus.Error;
                RaiseFault(conflict);
                return;
            }

            int timeoutSec = Math.Max(1, _app.Preferences.ConnectionTimeoutSec);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                ConnectionStatus = ConnectionStatus.Connecting;
                await _service.StartAsync(LocalAddress, localPort, multicastAddress, multicastPort, cts.Token);
                IsRunning = true;
            }
            catch (OperationCanceledException)
            {
                IsRunning = false;
                ConnectionStatus = ConnectionStatus.Idle;
                var timeoutMsg = LocalizationRegistry.Get("Udp.Msg_StartTimeout", timeoutSec);
                Log.Append(LogKind.Warn, timeoutMsg);
                RaiseFault(timeoutMsg);
            }
            catch (Exception ex)
            {
                IsRunning = false;
                ConnectionStatus = ConnectionStatus.Error;
                var failMsg = LocalizationRegistry.Get("Udp.Msg_StartFail", ex.Message);
                Log.Append(LogKind.Error, failMsg);
                RaiseFault(failMsg);
            }
        }

        [RelayCommand]
        private void Stop()
        {
            CancelPolling();
            IsPolling = false;
            _service.Stop();
            IsRunning = false;
        }

        [RelayCommand]
        private async Task SaveData()
        {
            await LogFileHelper.SaveAsync(Log, _fileDialogService, _notificationService, "Udp");
        }

        public override void Shutdown()
        {
            base.Shutdown();
            _service.BytesReceived -= OnBytesReceived;
            _service.DataSent -= OnDataSent;
            _service.StatusChanged -= OnStatusChanged;
            _service.ConnectionStateChanged -= OnConnectionStateChanged;
        }
    }
}
