using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.Services;

namespace FancyToolAva.ViewModels
{
    /// <summary>TCP 调试页面：会话集合之外只保留链路测试与页面级设置。</summary>
    public partial class TcpServerViewModel : TerminalPageViewModelBase<TcpSessionViewModel>, IViewState, IViewLifecycle
    {
        private readonly ISessionHub<ITcpServerService> _hub;
        private readonly TcpLinkTestService _linkTestService;
        private readonly IViewStateService _viewStateService;
        private readonly AppObserveModel _app;

        public TcpServerViewModel(ISessionHub<ITcpServerService> hub, TcpLinkTestService linkTestService,
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

        /// <summary>链路测试（带宽）独立模式的呈现状态。</summary>
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

        /// <summary>链路测试角色：0 = 接收端（服务器），1 = 发送端（客户端）。</summary>
        public int LinkRoleIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int LinkDurationSec
        {
            get;
            set => SetProperty(ref field, value);
        } = 10;

        /// <summary>链路测试发送块大小（KB）。</summary>
        public int LinkChunkKB
        {
            get;
            set => SetProperty(ref field, value);
        } = 64;

        // 链路测试用自己的端点，不跟随任何会话：切换会话不改变测试目标。
        public string LinkAddress
        {
            get;
            set => SetProperty(ref field, value);
        } = "127.0.0.1";

        public string LinkPort
        {
            get;
            set => SetProperty(ref field, value);
        } = "8080";

        public override string AggregateText => LocalizationRegistry.Get("Tcp.Label_Aggregate", ActiveCount, TotalSessions);

        string IViewState.ViewName => "tcpServerView";

        void IViewLifecycle.OnNavigatedTo() { }

        void IViewLifecycle.OnNavigatedFrom() { }

        object IViewState.CaptureState() => new TcpServerViewState
        {
            SchemaVersion = 2,
            Sessions = Sessions.Select(s => s.ToState()).ToList(),
            SelectedSessionId = CurrentSession?.Id,
            FunctionIndex = FunctionIndex,
            LinkAddress = LinkAddress,
            LinkPort = LinkPort,
            LinkRoleIndex = LinkRoleIndex,
            LinkDurationSec = LinkDurationSec,
            LinkChunkKB = LinkChunkKB
        };

        void IViewState.RestoreState(object state)
        {
            if (state is not TcpServerViewState saved) return;

            LinkRoleIndex = saved.LinkRoleIndex;
            LinkDurationSec = saved.LinkDurationSec;
            LinkChunkKB = saved.LinkChunkKB;

            var items = saved.Sessions;
            if (items.Count == 0)
            {
                // 旧版扁平存档：转成单会话，地址与端口同时作为链路测试端点。
                items =
                [
                    new TcpSessionViewState
                    {
                        ModeIndex = saved.ModeIndex,
                        Address = saved.Address,
                        Port = saved.Port,
                        SendText = saved.SendText,
                        IsHexSend = saved.IsHexSend,
                        IsHexDisplay = saved.IsHexDisplay,
                        EnableFrameBreak = saved.EnableFrameBreak,
                        FrameBreakInterval = saved.FrameBreakInterval,
                        PollInterval = saved.PollInterval,
                        EnableSendCount = saved.EnableSendCount,
                        SendCount = saved.SendCount
                    }
                ];
            }

            LinkAddress = string.IsNullOrWhiteSpace(saved.LinkAddress) ? items[0].Address : saved.LinkAddress;
            LinkPort = string.IsNullOrWhiteSpace(saved.LinkPort) ? items[0].Port : saved.LinkPort;

            ReplaceSessions(items, saved.SelectedSessionId);
            FunctionIndex = saved.FunctionIndex;
        }

        protected override void InitializeDefaultSession()
        {
            AddSession();
            SyncChoiceItems();
            SelectChoice(CurrentSession, reassert: true);
        }

        private TcpSessionViewModel AddSession(TcpSessionViewState? state = null, bool select = true)
        {
            var session = new TcpSessionViewModel(_hub.CreateSession(), _notificationService, _fileDialogService, _app)
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

        protected override TcpSessionViewModel? CreateSession(string? payload)
        {
            // 新会话复制当前会话、端口 +1：立即可启动，显示名通常不会与来源会话重名。
            var source = CurrentSession;
            if (source is null) return AddSession();
            var state = source.ToState();
            state.Id = string.Empty;
            state.Port = int.TryParse(source.Port, out int port) && port < 65535 ? (port + 1).ToString() : source.Port;
            return AddSession(state);
        }

        private void ReplaceSessions(List<TcpSessionViewState> items, string? selectedId)
        {
            foreach (var session in Sessions.ToArray())
            {
                RemoveSession(session);
            }
            Sessions.Clear();

            TcpSessionViewModel? selected = null;
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

        protected override void RemoveSession(TcpSessionViewModel session)
        {
            DetachSession(session);
            _hub.ReleaseSession(session.Service);
            Sessions.Remove(session);
        }

        protected override void SyncAddItems()
        {
            if (!Choices.Any(c => c.IsAddItem))
            {
                Choices.Add(SessionChoiceItem.ForAdd("Tcp.Menu_AddSession"));
            }
        }

        /// <summary>其它服务端模式、运行中或连接中的会话占了同一个绑定端点时拒绝启动。</summary>
        private bool IsEndpointBlocked(TcpSessionViewModel self, BindEndpoint candidate)
        {
            return EndpointConflict.Tcp(candidate, Sessions
                .Where(s => !ReferenceEquals(s, self) && s.ModeIndex == 0 && IsStartingOrRunning(s))
                .Select(s => BindEndpoint.ForTcp(s.Address, int.TryParse(s.Port, out int port) ? port : 0)));
        }

        private static bool IsStartingOrRunning(TcpSessionViewModel session)
            => session.IsListening || session.ConnectionStatus == ConnectionStatus.Connecting;

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
            if (!int.TryParse(LinkPort, out int portNum) || portNum < 1 || portNum > 65535)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Tcp.Msg_InvalidPort"));
                return;
            }

            // 调试流量会干扰带宽测量，开始测试时停止全部运行中的会话。
            int stopped = StopAllSessions();
            if (stopped > 0)
            {
                _notificationService.ShowInfo(LocalizationRegistry.Get("Tcp.Msg_LinkTestStoppedSessions", stopped));
            }

            bool isSender = LinkRoleIndex == 1;
            LinkTest.Begin(isSender, LinkDurationSec, hasLossStats: false);
            var options = new LinkTestOptions
            {
                DurationSec = LinkDurationSec,
                ChunkSize = LinkChunkKB * 1024,
                ConnectTimeoutSec = Math.Max(1, _app.Preferences.ConnectionTimeoutSec),
            };

            try
            {
                if (isSender)
                {
                    await _linkTestService.StartSenderAsync(LinkAddress, portNum, options);
                }
                else
                {
                    await _linkTestService.StartReceiverAsync(LinkAddress, portNum);
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
