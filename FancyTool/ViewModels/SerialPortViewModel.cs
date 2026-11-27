using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.Services;
using Lang.Avalonia;

namespace FancyToolAva.ViewModels
{
    /// <summary>
    /// 串口调试页面：管理多个会话，ComboBox 同时承担"会话选择"和"添加端口"。
    /// 页面保持单栏布局，日志/发送/参数展示当前选中会话。
    /// </summary>
    public partial class SerialPortViewModel : TerminalPageViewModelBase<SerialSessionViewModel>, IViewState, IViewLifecycle
    {
        private readonly ISerialPortHub _hub;
        private readonly IViewStateService _viewStateService;
        private readonly AppObserveModel _app;
        private readonly Dictionary<SerialSessionViewModel, ISerialPortService> _services = [];

        public SerialPortViewModel(ISerialPortHub hub, INotificationService notificationService,
            IViewStateService viewStateService, IFileDialogService fileDialogService, AppObserveModel app,
            TerminalTicker ticker) : base(ticker, notificationService, fileDialogService)
        {
            _hub = hub;
            _viewStateService = viewStateService;
            _app = app;

            InitializeDefaultSession();
            _viewStateService.Register(this);
        }

        public ObservableCollection<string> AvailablePorts { get; } = [];

        public IReadOnlyList<string> BaudRates => SerialSessionViewModel.BaudRates;
        public IReadOnlyList<string> Parities => SerialSessionViewModel.Parities;
        public IReadOnlyList<string> DataBitsList => SerialSessionViewModel.DataBitsList;
        public IReadOnlyList<string> StopBitsList => SerialSessionViewModel.StopBitsList;
        public IReadOnlyList<string> Encodings => SerialSessionViewModel.Encodings;

        public override string AggregateText => LocalizationRegistry.Get("Serial.Label_Aggregate", ActiveCount, TotalSessions);

        string IViewState.ViewName => "serialPortView";

        void IViewLifecycle.OnNavigatedTo() { }

        void IViewLifecycle.OnNavigatedFrom() { }

        object IViewState.CaptureState() => new SerialPortViewState
        {
            SchemaVersion = 2,
            Sessions = Sessions.Select(s => s.ToState()).ToList(),
            SelectedSessionId = CurrentSession?.Id
        };

        void IViewState.RestoreState(object state)
        {
            if (state is not SerialPortViewState saved) return;

            var items = saved.Sessions;
            if (items.Count == 0 && !string.IsNullOrEmpty(saved.SelectedPort))
            {
                items =
                [
                    new SerialSessionViewState
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        PortName = saved.SelectedPort,
                        BaudRateIndex = saved.BaudRateIndex,
                        ParityIndex = saved.ParityIndex,
                        DataBitsIndex = saved.DataBitsIndex,
                        StopBitsIndex = saved.StopBitsIndex,
                        EncodingIndex = saved.EncodingIndex,
                        FrameBreakInterval = saved.FrameBreakInterval,
                        IsHexSend = saved.IsHexSend,
                        IsHexDisplay = saved.IsHexDisplay,
                        IsRtsEnabled = saved.IsRtsEnabled,
                        IsDtrEnabled = saved.IsDtrEnabled,
                        PollInterval = saved.PollInterval,
                        EnableSendCount = saved.EnableSendCount,
                        SendCount = saved.SendCount,
                        SendText = saved.SendText
                    }
                ];
            }

            if (items.Count == 0) return;

            ReplaceSessions(items, saved.SelectedSessionId);
        }

        protected override void InitializeDefaultSession()
        {
            RefreshPortsCore();
            string port = AvailablePorts.FirstOrDefault() ?? string.Empty;
            AddSession(port);
            SyncChoiceItems();
            SelectChoice(CurrentSession, reassert: true);
        }

        private SerialSessionViewModel? AddSession(string portName, SerialSessionViewState? state = null, bool select = true)
        {
            var service = _hub.CreateSession();
            var session = new SerialSessionViewModel(service, _notificationService, _fileDialogService, _app)
            {
                SelectedPort = portName,
                PortConflictCheck = IsPortBlocked
            };
            if (state is not null)
            {
                session.ApplyState(state);
            }

            _services[session] = service;
            RegisterSession(session, select);
            return session;
        }

        protected override SerialSessionViewModel? CreateSession(string? payload)
        {
            return AddSession(payload ?? string.Empty);
        }

        private void ReplaceSessions(List<SerialSessionViewState> items, string? selectedId)
        {
            foreach (var session in Sessions.ToArray())
            {
                RemoveSession(session);
            }
            Sessions.Clear();

            SerialSessionViewModel? selected = null;
            foreach (var item in items)
            {
                var created = AddSession(item.PortName, item, select: false);
                if (created is not null && string.Equals(created.Id, selectedId, StringComparison.Ordinal))
                {
                    selected = created;
                }
            }

            CurrentSession = selected ?? Sessions.FirstOrDefault();
            SyncChoiceItems();
            SelectChoice(CurrentSession, reassert: true);
        }

        protected override void RemoveSession(SerialSessionViewModel session)
        {
            DetachSession(session);

            if (_services.Remove(session, out var service))
            {
                _hub.ReleaseSession(service);
            }

            Sessions.Remove(session);
        }

        private bool IsPortBlocked(SerialSessionViewModel self, string port)
        {
            return Sessions.Any(other => !ReferenceEquals(other, self)
                && string.Equals(other.SelectedPort, port, StringComparison.OrdinalIgnoreCase)
                && (other.IsConnected || other.ConnectionStatus == ConnectionStatus.Connecting));
        }

        /// <summary>下拉末尾的"添加端口"项：系统枚举出的端口减去已被会话占用的。</summary>
        protected override void SyncAddItems()
        {
            var used = Sessions
                .Select(s => s.SelectedPort)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (int i = Choices.Count - 1; i >= 0; i--)
            {
                var item = Choices[i];
                if (item.IsAddItem && item.AddPayload is { } port && used.Contains(port))
                {
                    Choices.RemoveAt(i);
                }
            }

            var present = Choices
                .Where(c => c.IsAddItem)
                .Select(c => c.AddPayload)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var port in AvailablePorts)
            {
                if (string.IsNullOrEmpty(port) || used.Contains(port) || present.Contains(port)) continue;
                Choices.Add(SessionChoiceItem.ForAdd("Serial.Menu_AddPort", port));
            }
        }

        private void RefreshPortsCore()
        {
            AvailablePorts.Clear();
            foreach (var port in _hub.GetAvailablePorts())
            {
                AvailablePorts.Add(port);
            }
        }

        protected override Task SaveCurrentSessionLogAsync()
        {
            return CurrentSession?.SaveDataCommand.ExecuteAsync(null) ?? Task.CompletedTask;
        }

        [RelayCommand]
        private void RefreshPorts()
        {
            RefreshPortsCore();
            SyncChoiceItems();
            SelectChoice(CurrentSession, reassert: true);
        }
    }
}
