using System.Collections.ObjectModel;
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
    /// TCP 会话：一路服务端监听或一路客户端连接。参数、日志、计数、已连接客户端与定时发送都归这一路自己。
    /// </summary>
    public partial class TcpSessionViewModel : TerminalSessionViewModelBase
    {
        private readonly ITcpServerService _service;
        private readonly IFileDialogService _fileDialogService;
        private readonly AppObserveModel _app;
        private string? _pollTarget;

        public TcpSessionViewModel(ITcpServerService service, INotificationService notificationService,
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

        /// <summary>页面注入的端点冲突检查：与其它运行中的服务端会话互斥时返回 true。</summary>
        public Func<TcpSessionViewModel, BindEndpoint, bool>? EndpointConflictCheck { get; set; }

        public ITcpServerService Service => _service;

        public override bool IsActive => IsListening || IsConnected;

        private string _displayName = string.Empty;

        // 下拉项在每个属性变化时都会重读显示名，所以随输入更新并缓存，不在读取时格式化。
        public override string DisplayName => _displayName;

        private void RefreshDisplayName()
        {
            SetProperty(ref _displayName,
                LocalizationRegistry.Get(ModeIndex == 0 ? "Tcp.Status_Server" : "Tcp.Status_Client", Address, Port),
                nameof(DisplayName));
        }

        protected override string NotActiveMessageKey => "Tcp.Msg_NotActive";

        protected override string TimedSendErrorMessageKey => "Tcp.Msg_TimedSendError";

        private string ReadyText => LocalizationRegistry.Get(ModeIndex == 0 ? "Tcp.Status_ReadyServer" : "Tcp.Status_ReadyClient");

        public int ModeIndex
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    RefreshDisplayName();
                    if (!IsActive) StatusText = ReadyText;
                }
            }
        }

        public string Address
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) RefreshDisplayName();
            }
        } = "127.0.0.1";

        public string Port
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) RefreshDisplayName();
            }
        } = "8080";

        /// <summary>服务端监听中；客户端会话连接后看 <see cref="IsConnected"/>。</summary>
        public bool IsListening
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) OnActiveChanged();
            }
        }

        public bool IsConnected
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) OnActiveChanged();
            }
        }

        public ObservableCollection<string> ConnectedClients => _service.ConnectedClients;

        /// <summary>服务端模式下选中的下挂客户端，发送时只发给它；为空表示发给全部。</summary>
        public string SelectedClient
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public bool EnableFrameBreak
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) _service.EnableFrameBreak = value;
            }
        }

        public int FrameBreakInterval
        {
            get;
            set
            {
                if (SetProperty(ref field, value)) _service.SetFrameBreakInterval(value);
            }
        } = 20;

        public override void RefreshCulture()
        {
            base.RefreshCulture();
            RefreshDisplayName();
            if (IsActive)
            {
                StatusDetail = DisplayName;
            }
            else
            {
                StatusText = ReadyText;
            }
        }

        public TcpSessionViewState ToState() => new()
        {
            Id = Id,
            ModeIndex = ModeIndex,
            Address = Address,
            Port = Port,
            SendText = SendText,
            IsHexSend = IsHexSend,
            IsHexDisplay = IsHexDisplay,
            EnableFrameBreak = EnableFrameBreak,
            FrameBreakInterval = FrameBreakInterval,
            PollInterval = PollInterval,
            EnableSendCount = EnableSendCount,
            SendCount = SendCount
        };

        public void ApplyState(TcpSessionViewState state)
        {
            RestoreId(state.Id);
            ModeIndex = state.ModeIndex == 1 ? 1 : 0;
            Address = state.Address;
            Port = state.Port;
            SendText = state.SendText;
            IsHexSend = state.IsHexSend;
            IsHexDisplay = state.IsHexDisplay;
            EnableFrameBreak = state.EnableFrameBreak;
            FrameBreakInterval = state.FrameBreakInterval <= 0 ? 20 : state.FrameBreakInterval;
            PollInterval = state.PollInterval <= 0 ? 1000 : state.PollInterval;
            EnableSendCount = state.EnableSendCount;
            SendCount = state.SendCount <= 0 ? 1 : state.SendCount;
        }

        protected override string DecodeBytes(byte[] bytes) => Encoding.UTF8.GetString(bytes);

        private void OnActiveChanged()
        {
            OnPropertyChanged(nameof(IsActive));
            if (IsActive)
            {
                ConnectionStatus = ConnectionStatus.Connected;
                StatusDetail = DisplayName;
                StartElapsed();
                return;
            }

            ConnectionStatus = ConnectionStatus.Idle;
            StatusDetail = string.Empty;
            StopElapsed();
            _pollTarget = null;
            CancelPolling();
        }

        /// <summary>服务端：选中了客户端就只发给它，没选中才发给全部已连接客户端；客户端：无目标。</summary>
        private string? ResolveTarget()
            => ModeIndex == 0 && !string.IsNullOrEmpty(SelectedClient) ? SelectedClient : null;

        public override async Task SendManualAsync()
        {
            if (string.IsNullOrEmpty(SendText)) return;
            if (!IsActive)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get(
                    ModeIndex == 0 ? "Tcp.Msg_ServerNotStarted" : "Tcp.Msg_ClientNotConnected"));
                return;
            }

            try
            {
                await _service.SendAsync(SendText, IsHexSend, ResolveTarget()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var errMsg = LocalizationRegistry.Get("Tcp.Msg_SendFail", ex.Message);
                Log.Enqueue(LogKind.Error, errMsg);
                RaiseFault(errMsg);
            }
        }

        protected override Func<Task> CreatePollingSender()
        {
            // 目标在开始时确定：之后更换列表选择不影响进行中的定时发送，要换目标就停止后重新开始。
            string? target = ResolveTarget();
            _pollTarget = target;
            return () => _service.SendAsync(SendText, IsHexSend, target);
        }

        private void StopPollingIfTargetGone(string? endpoint)
        {
            if (!IsPolling || endpoint is null || _pollTarget != endpoint) return;
            _pollTarget = null;
            CancelPolling();
            Log.Append(LogKind.Warn, LocalizationRegistry.Get("Tcp.Msg_PollTargetGone", endpoint));
        }

        /// <summary>下挂客户端离开：清掉指向它的选择，否则服务端会一直"只发给"一个已不存在的客户端。</summary>
        private void OnClientLeft(string? endpoint)
        {
            if (endpoint is null) return;
            StopPollingIfTargetGone(endpoint);
            if (string.Equals(SelectedClient, endpoint, StringComparison.Ordinal))
            {
                SelectedClient = string.Empty;
            }
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
                        if (ModeIndex == 1) IsConnected = true;
                        else IsListening = true;
                        Log.Append(LogKind.System, e.Message);
                        break;
                    case ConnectionEventType.Disconnected:
                        IsListening = false;
                        IsConnected = false;
                        IsPolling = false;
                        CancelPolling();
                        Log.Append(LogKind.System, e.Message);
                        break;
                    case ConnectionEventType.ClientConnected:
                        Log.Append(LogKind.System, e.Message);
                        break;
                    case ConnectionEventType.ClientDisconnected:
                        Log.Append(LogKind.System, e.Message);
                        OnClientLeft(e.Endpoint);
                        break;
                    case ConnectionEventType.Lost:
                        // 服务端下挂的单个客户端出错时带 Endpoint，监听本身没有停。
                        if (ModeIndex == 0 && e.Endpoint is not null)
                        {
                            Log.Append(LogKind.Error, e.Message);
                            OnClientLeft(e.Endpoint);
                            break;
                        }
                        IsListening = false;
                        IsConnected = false;
                        IsPolling = false;
                        CancelPolling();
                        Log.Append(LogKind.Error, e.Message);
                        break;
                    case ConnectionEventType.PingTimeout:
                        Log.Append(LogKind.Warn, e.Message);
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
            if (!int.TryParse(Port, out int portNum) || portNum < 1 || portNum > 65535)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Tcp.Msg_InvalidPort"));
                return;
            }

            var endpoint = BindEndpoint.ForTcp(Address, portNum);
            if (ModeIndex == 0 && EndpointConflictCheck?.Invoke(this, endpoint) == true)
            {
                var conflict = LocalizationRegistry.Get("Tcp.Msg_EndpointInUse", endpoint.ToString());
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

                if (ModeIndex == 0)
                {
                    await _service.StartServerAsync(Address, portNum, cts.Token);
                    IsListening = _service.IsRunning;
                }
                else
                {
                    await _service.ConnectClientAsync(Address, portNum, cts.Token);
                    IsConnected = true;
                }
            }
            catch (OperationCanceledException)
            {
                IsListening = false;
                IsConnected = false;
                ConnectionStatus = ConnectionStatus.Idle;
                var timeoutMsg = LocalizationRegistry.Get("Tcp.Msg_Timeout", timeoutSec);
                Log.Append(LogKind.Warn, timeoutMsg);
                RaiseFault(timeoutMsg);
            }
            catch (Exception ex)
            {
                IsListening = false;
                IsConnected = false;
                ConnectionStatus = ConnectionStatus.Error;
                var failMsg = LocalizationRegistry.Get("Tcp.Msg_StartFail", ex.Message);
                Log.Append(LogKind.Error, failMsg);
                RaiseFault(failMsg);
            }
        }

        [RelayCommand]
        private void Stop()
        {
            CancelPolling();
            IsPolling = false;
            if (ModeIndex == 0)
            {
                _service.StopServer();
                IsListening = false;
            }
            else
            {
                _service.DisconnectClient();
                IsConnected = false;
            }
        }

        [RelayCommand]
        private void KickClient()
        {
            if (string.IsNullOrEmpty(SelectedClient))
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Tcp.Msg_NoClientSelected"));
                return;
            }
            _service.DisconnectClient(SelectedClient);
            SelectedClient = string.Empty;
        }

        [RelayCommand]
        private async Task SaveData()
        {
            await LogFileHelper.SaveAsync(Log, _fileDialogService, _notificationService, "Tcp");
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
