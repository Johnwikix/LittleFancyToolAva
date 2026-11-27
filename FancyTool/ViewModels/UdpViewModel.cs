using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.Services;

namespace FancyToolAva.ViewModels
{
    /// <summary>UDP 调试页面：会话集合之外只保留链路测试与页面级设置。</summary>
    public partial class UdpViewModel : TerminalPageViewModelBase<UdpSessionViewModel>, IViewState, IViewLifecycle
    {
        private readonly ISessionHub<IUdpService> _hub;
        private readonly UdpLinkTestService _linkTestService;
        private readonly IViewStateService _viewStateService;
        private readonly AppObserveModel _app;

        public UdpViewModel(ISessionHub<IUdpService> hub, UdpLinkTestService linkTestService,
            INotificationService notificationService, IViewStateService viewStateService,
            IFileDialogService fileDialogService, AppObserveModel app, TerminalTicker ticker)
            : base(ticker, notificationService, fileDialogService)
        {
            _hub = hub;
            _linkTestService = linkTestService;
            _viewStateService = viewStateService;
            _app = app;

            LinkTest = new LinkTestPresenter(linkTestService);

            InitializeDefaultSession();
            _viewStateService.Register(this);
        }

        /// <summary>链路测试（带宽 / 丢包率）独立模式的呈现状态。</summary>
        public LinkTestPresenter LinkTest { get; }

        /// <summary>页面功能：0 = 通信调试，1 = 链路测试。</summary>
        public int FunctionIndex
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnFunctionIndexChanged(value);
                }
            }
        }

        /// <summary>链路测试角色：0 = 接收端，1 = 发送端。</summary>
        public int LinkRoleIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        /// <summary>链路测试时长（秒）。</summary>
        public int LinkDurationSec
        {
            get;
            set => SetProperty(ref field, value);
        } = 10;

        /// <summary>链路测试 UDP 包大小（字节，含协议头）。</summary>
        public int LinkPacketSize
        {
            get;
            set => SetProperty(ref field, value);
        } = 1400;

        /// <summary>目标发送速率（Mbps）；0 = 不限速。</summary>
        public int LinkRateMbps
        {
            get;
            set => SetProperty(ref field, value);
        } = 10;

        // 链路测试用自己的端点，不跟随任何会话：切换会话不改变测试目标。
        public string LinkLocalAddress
        {
            get;
            set => SetProperty(ref field, value);
        } = "0.0.0.0";

        public string LinkLocalPort
        {
            get;
            set => SetProperty(ref field, value);
        } = "8080";

        public string LinkRemoteAddress
        {
            get;
            set => SetProperty(ref field, value);
        } = "127.0.0.1";

        public string LinkRemotePort
        {
            get;
            set => SetProperty(ref field, value);
        } = "9090";

        public override string AggregateText => LocalizationRegistry.Get("Udp.Label_Aggregate", ActiveCount, TotalSessions);

        string IViewState.ViewName => "udpView";

        void IViewLifecycle.OnNavigatedTo() { }

        void IViewLifecycle.OnNavigatedFrom() { }

        object IViewState.CaptureState() => new UdpViewState
        {
            SchemaVersion = 2,
            Sessions = Sessions.Select(s => s.ToState()).ToList(),
            SelectedSessionId = CurrentSession?.Id,
            FunctionIndex = FunctionIndex,
            LinkLocalAddress = LinkLocalAddress,
            LinkLocalPort = LinkLocalPort,
            LinkRemoteAddress = LinkRemoteAddress,
            LinkRemotePort = LinkRemotePort,
            LinkRoleIndex = LinkRoleIndex,
            LinkDurationSec = LinkDurationSec,
            LinkPacketSize = LinkPacketSize,
            LinkRateMbps = LinkRateMbps
        };

        void IViewState.RestoreState(object state)
        {
            if (state is not UdpViewState saved) return;

            LinkRoleIndex = saved.LinkRoleIndex;
            LinkDurationSec = saved.LinkDurationSec;
            LinkPacketSize = saved.LinkPacketSize;
            LinkRateMbps = saved.LinkRateMbps;

            var items = saved.Sessions;
            if (items.Count == 0)
            {
                // 旧版扁平存档：转成单会话。
                items =
                [
                    new UdpSessionViewState
                    {
                        ModeIndex = saved.ModeIndex,
                        LocalAddress = saved.LocalAddress,
                        LocalPort = saved.LocalPort,
                        MulticastAddress = saved.MulticastAddress,
                        MulticastPort = saved.MulticastPort,
                        RemoteAddress = saved.RemoteAddress,
                        RemotePort = saved.RemotePort,
                        SendText = saved.SendText,
                        IsHexSend = saved.IsHexSend,
                        IsHexDisplay = saved.IsHexDisplay,
                        PollInterval = saved.PollInterval,
                        EnableSendCount = saved.EnableSendCount,
                        SendCount = saved.SendCount
                    }
                ];
            }

            // 旧存档没有链路测试端点：接收端取旧会话的本地端点，发送端取旧的远程端点。
            LinkLocalAddress = OrSaved(saved.LinkLocalAddress, items[0].LocalAddress);
            LinkLocalPort = OrSaved(saved.LinkLocalPort, items[0].LocalPort);
            LinkRemoteAddress = OrSaved(saved.LinkRemoteAddress, items[0].RemoteAddress);
            LinkRemotePort = OrSaved(saved.LinkRemotePort, items[0].RemotePort);

            ReplaceSessions(items, saved.SelectedSessionId);
            FunctionIndex = saved.FunctionIndex;
        }

        private static string OrSaved(string saved, string fallback)
            => string.IsNullOrWhiteSpace(saved) ? fallback : saved;

        protected override void InitializeDefaultSession()
        {
            AddSession();
            SyncChoiceItems();
            SelectChoice(CurrentSession, reassert: true);
        }

        private UdpSessionViewModel AddSession(UdpSessionViewState? state = null, bool select = true)
        {
            var session = new UdpSessionViewModel(_hub.CreateSession(), _notificationService, _fileDialogService, _app)
            {
                EndpointConflictCheck = IsEndpointBlocked
            };
            if (state is not null)
            {
                session.ApplyState(state);
            }

            RegisterSession(session, select);
            return session;
        }

        protected override UdpSessionViewModel? CreateSession(string? payload)
        {
            // 新会话复制当前会话、本地端口 +1：立即可启动，显示名通常不会与来源会话重名。
            var source = CurrentSession;
            if (source is null) return AddSession();
            var state = source.ToState();
            state.Id = string.Empty;
            state.LocalPort = int.TryParse(source.LocalPort, out int port) && port < 65535 ? (port + 1).ToString() : source.LocalPort;
            return AddSession(state);
        }

        private void ReplaceSessions(List<UdpSessionViewState> items, string? selectedId)
        {
            foreach (var session in Sessions.ToArray())
            {
                RemoveSession(session);
            }
            Sessions.Clear();

            UdpSessionViewModel? selected = null;
            foreach (var item in items)
            {
                var created = AddSession(item, select: false);
                if (string.Equals(created.Id, selectedId, StringComparison.Ordinal))
                {
                    selected = created;
                }
            }

            CurrentSession = selected ?? Sessions.FirstOrDefault();
            SyncChoiceItems();
            SelectChoice(CurrentSession, reassert: true);
        }

        protected override void RemoveSession(UdpSessionViewModel session)
        {
            DetachSession(session);
            _hub.ReleaseSession(session.Service);
            Sessions.Remove(session);
        }

        protected override void SyncAddItems()
        {
            if (!Choices.Any(c => c.IsAddItem))
            {
                Choices.Add(SessionChoiceItem.ForAdd("Udp.Menu_AddSession"));
            }
        }

        /// <summary>
        /// 普通会话与任何运行中的会话互斥（ReuseAddress 让同端口双绑定成功，投递归属不确定）；
        /// 组播会话只与运行中的非组播会话互斥，同一端口上的多个组播会话各收一份，是合法用法。
        /// </summary>
        private bool IsEndpointBlocked(UdpSessionViewModel self, BindEndpoint candidate)
        {
            return EndpointConflict.Udp(candidate, self.IsMulticast, Sessions
                .Where(s => !ReferenceEquals(s, self) && IsStartingOrRunning(s))
                .Select(s => (BindEndpoint.ForUdp(s.LocalAddress, int.TryParse(s.LocalPort, out int port) ? port : 0), s.IsMulticast)));
        }

        private static bool IsStartingOrRunning(UdpSessionViewModel session)
            => session.IsRunning || session.ConnectionStatus == ConnectionStatus.Connecting;

        protected override Task SaveCurrentSessionLogAsync()
        {
            return CurrentSession is { } session ? session.SaveDataCommand.ExecuteAsync(null) : Task.CompletedTask;
        }

        private void OnFunctionIndexChanged(int value)
        {
            // 切到链路测试不再停止任何会话；切回通信调试时停止在途测试。
            if (value != 1)
            {
                _ = _linkTestService.StopAsync();
            }
        }

        [RelayCommand]
        private async Task StartLinkTest()
        {
            if (LinkTest.IsRunning) return;
            bool isSender = LinkRoleIndex == 1;

            // 发送端校验目标地址端口，接收端校验本地绑定端口
            string portText = isSender ? LinkRemotePort : LinkLocalPort;
            if (!int.TryParse(portText, out int portNum) || portNum < 1 || portNum > 65535)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get(
                    isSender ? "Udp.Msg_InvalidRemotePort" : "Udp.Msg_InvalidLocalPort"));
                return;
            }
            if (LinkPacketSize < LinkTestUdpProtocol.MinPacketSize || LinkPacketSize > LinkTestUdpProtocol.MaxPacketSize)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get(
                    "LinkTest.Msg_InvalidPacketSize",
                    LinkTestUdpProtocol.MinPacketSize,
                    LinkTestUdpProtocol.MaxPacketSize));
                return;
            }

            // 调试流量会干扰带宽与丢包测量，开始测试时停止全部运行中的会话。
            int stopped = StopAllSessions();
            if (stopped > 0)
            {
                _notificationService.ShowInfo(LocalizationRegistry.Get("Udp.Msg_LinkTestStoppedSessions", stopped));
            }

            LinkTest.Begin(isSender, LinkDurationSec, hasLossStats: true);
            var options = new LinkTestOptions
            {
                DurationSec = LinkDurationSec,
                ChunkSize = LinkPacketSize,
                RateLimitBps = LinkRateMbps <= 0 ? 0 : (long)Math.Round(LinkRateMbps * 1_000_000.0 / 8.0),
            };

            try
            {
                if (isSender)
                {
                    await _linkTestService.StartSenderAsync(LinkRemoteAddress, portNum, options);
                }
                else
                {
                    await _linkTestService.StartReceiverAsync(LinkLocalAddress, portNum);
                }
            }
            catch (Exception ex)
            {
                var failMsg = LocalizationRegistry.Get("LinkTest.Msg_StartFail", ex.Message);
                LinkTest.Fail(failMsg);
                _notificationService.ShowError(failMsg);
            }
        }

        private int StopAllSessions()
        {
            var running = Sessions.Where(s => s.IsActive || s.ConnectionStatus == ConnectionStatus.Connecting).ToArray();
            foreach (var session in running)
            {
                session.StopCommand.Execute(null);
            }
            return running.Length;
        }

        [RelayCommand]
        private async Task StopLinkTest()
        {
            await _linkTestService.StopAsync();
        }
    }
}
