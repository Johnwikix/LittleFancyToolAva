using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lang.Avalonia;
using FancyToolAva.Models;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.Services;
using FancyToolAva.Utils;

namespace FancyToolAva.ViewModels
{
    /// <summary>
    /// 串口调试页面：管理多个会话，ComboBox 同时承担"会话选择"和"添加端口"。
    /// 页面保持单栏布局，日志/发送/参数展示当前选中会话。
    /// </summary>
    public partial class SerialPortViewModel : ViewModelBase, IViewState, IViewLifecycle
    {
        public const int MaxSessions = 8;

        private readonly ISerialPortHub _hub;
        private readonly INotificationService _notificationService;
        private readonly IViewStateService _viewStateService;
        private readonly IFileDialogService _fileDialogService;
        private readonly AppObserveModel _app;
        private readonly Dictionary<SerialSessionViewModel, ISerialPortService> _services = [];
        private readonly DispatcherTimer _uiFlushTimer;
        private readonly DispatcherTimer _elapsedTimer;

        private bool _suppressChoice;
        private SerialChoiceItem? _selectedChoice;
        private SerialSessionViewModel? _currentSession;
        private bool _isBroadcast;
        private bool _isMergedLog;
        private bool _canCloseCached = true;

        public SerialPortViewModel(ISerialPortHub hub, INotificationService notificationService,
            IViewStateService viewStateService, IFileDialogService fileDialogService, AppObserveModel app)
        {
            _hub = hub;
            _notificationService = notificationService;
            _viewStateService = viewStateService;
            _fileDialogService = fileDialogService;
            _app = app;

            MergedLog.MaxLines = 20_000;

            _uiFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _uiFlushTimer.Tick += (_, _) => FlushAllSessions();
            _uiFlushTimer.Start();

            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += (_, _) => TickSessions();
            _elapsedTimer.Start();

            InitializeDefaultSession();
            _viewStateService.Register(this);
        }

        public ObservableCollection<SerialSessionViewModel> Sessions { get; } = [];

        public ObservableCollection<SerialChoiceItem> PortChoices { get; } = [];

        public ObservableCollection<string> AvailablePorts { get; } = [];

        public LogBuffer MergedLog { get; } = new(autoFlush: false);

        public IReadOnlyList<string> BaudRates => SerialSessionViewModel.BaudRates;
        public IReadOnlyList<string> Parities => SerialSessionViewModel.Parities;
        public IReadOnlyList<string> DataBitsList => SerialSessionViewModel.DataBitsList;
        public IReadOnlyList<string> StopBitsList => SerialSessionViewModel.StopBitsList;
        public IReadOnlyList<string> Encodings => SerialSessionViewModel.Encodings;

        public SerialSessionViewModel? CurrentSession
        {
            get => _currentSession;
            private set
            {
                if (ReferenceEquals(_currentSession, value)) return;
                _currentSession?.SetActive(false);
                _currentSession = value;
                _currentSession?.SetActive(true);
                _currentSession?.MarkRead();
                OnPropertyChanged();
                OnPropertyChanged(string.Empty);
                SelectChoice(value);
                RefreshAggregate();
            }
        }

        public SerialChoiceItem? SelectedChoice
        {
            get => _selectedChoice;
            set
            {
                if (_suppressChoice) return;

                if (value is null)
                {
                    // ItemsSource 变更（选中项被移出集合）时控件会先推 null。
                    // 这里不把 null 写回字段，改为重新推送当前会话的选中项，避免收起框变空。
                    ReassertSelection();
                    return;
                }

                if (ReferenceEquals(_selectedChoice, value)) return;

                _selectedChoice = value;
                OnPropertyChanged();

                if (value.Session is { } session)
                {
                    CurrentSession = session;
                    return;
                }

                var created = AddSession(value.PortName);
                SyncChoiceItems();
                if (created is not null)
                {
                    SelectChoice(created, reassert: true);
                }
            }
        }

        public bool IsBroadcast
        {
            get => _isBroadcast;
            set
            {
                if (SetProperty(ref _isBroadcast, value))
                {
                    OnPropertyChanged(nameof(CanSend));
                }
            }
        }

        public int TotalSessions => Sessions.Count;

        public int ConnectedCount => Sessions.Count(s => s.IsConnected);

        public bool IsAnyConnected => Sessions.Any(s => s.IsConnected);

        public bool CanSend => IsBroadcast ? IsAnyConnected : CurrentSession?.IsConnected == true;

        public bool CanCloseSession => Sessions.Count > 1;

        public bool CanAddSession => Sessions.Count < MaxSessions;

        public string AggregateText => LocalizationRegistry.Get("Serial.Label_Aggregate", ConnectedCount, TotalSessions);

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

        private void InitializeDefaultSession()
        {
            RefreshPortsCore();
            string port = AvailablePorts.FirstOrDefault() ?? string.Empty;
            AddSession(port);
            SyncChoiceItems();
            SelectChoice(CurrentSession, reassert: true);
        }

        private SerialSessionViewModel? AddSession(string portName, SerialSessionViewState? state = null, bool select = true)
        {
            if (Sessions.Count >= MaxSessions)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Serial.Msg_MaxSessions", MaxSessions));
                return null;
            }

            var service = _hub.CreateSession();
            var session = new SerialSessionViewModel(service, _notificationService, _fileDialogService, _app)
            {
                SelectedPort = portName,
                PortConflictCheck = IsPortBlocked
            };
            session.Faulted += OnSessionFaulted;
            session.PropertyChanged += OnSessionPropertyChanged;
            session.LogSink = OnSessionLog;
            if (state is not null)
            {
                session.ApplyState(state);
            }

            _services[session] = service;
            Sessions.Add(session);
            if (select)
            {
                CurrentSession = session;
            }
            RefreshAggregate();
            return session;
        }

        private void ReplaceSessions(List<SerialSessionViewState> items, string? selectedId)
        {
            foreach (var session in Sessions.ToArray())
            {
                RemoveSession(session, releaseHub: true);
            }
            Sessions.Clear();

            SerialSessionViewModel? selected = null;
            foreach (var item in items.Take(MaxSessions))
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

        private void RemoveSession(SerialSessionViewModel session, bool releaseHub)
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
            session.Faulted -= OnSessionFaulted;
            session.Shutdown();

            if (_services.Remove(session, out var service) && releaseHub)
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

        /// <summary>
        /// 增量同步下拉项：不整表 Clear（Clear 会让 ComboBox 把 SelectedItem 置空，
        /// 且新实例不在旧集合里，收起框会停在"未选中"状态）。
        /// </summary>
        private void SyncChoiceItems()
        {
            _suppressChoice = true;
            try
            {
                for (int i = PortChoices.Count - 1; i >= 0; i--)
                {
                    if (PortChoices[i].Session is { } existing && !Sessions.Contains(existing))
                    {
                        PortChoices.RemoveAt(i);
                    }
                }

                foreach (var session in Sessions)
                {
                    if (PortChoices.Any(c => ReferenceEquals(c.Session, session))) continue;
                    var item = SerialChoiceItem.ForSession(session);
                    int addIndex = IndexOfFirstAddPortItem();
                    if (addIndex >= 0) PortChoices.Insert(addIndex, item);
                    else PortChoices.Add(item);
                }

                var used = Sessions
                    .Select(s => s.SelectedPort)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                for (int i = PortChoices.Count - 1; i >= 0; i--)
                {
                    var item = PortChoices[i];
                    if (item.Session is null && used.Contains(item.PortName))
                    {
                        PortChoices.RemoveAt(i);
                    }
                }

                var present = PortChoices
                    .Where(c => c.Session is null)
                    .Select(c => c.PortName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var port in AvailablePorts)
                {
                    if (string.IsNullOrEmpty(port) || used.Contains(port) || present.Contains(port)) continue;
                    PortChoices.Add(SerialChoiceItem.ForPort(port));
                }
            }
            finally
            {
                _suppressChoice = false;
            }

            OnPropertyChanged(nameof(CanAddSession));
        }

        private int IndexOfFirstAddPortItem()
        {
            for (int i = 0; i < PortChoices.Count; i++)
            {
                if (PortChoices[i].Session is null) return i;
            }
            return -1;
        }

        private SerialChoiceItem? FindItem(SerialSessionViewModel? session)
        {
            if (session is null) return null;
            return PortChoices.FirstOrDefault(c => ReferenceEquals(c.Session, session));
        }

        private void SelectChoice(SerialSessionViewModel? session, bool reassert = false)
        {
            var item = FindItem(session);
            if (item is null) return;

            if (!ReferenceEquals(_selectedChoice, item))
            {
                _selectedChoice = item;
                OnPropertyChanged(nameof(SelectedChoice));
            }

            if (!reassert) return;

            // 集合刚变更过时，ComboBox 可能在本帧稍后才把 SelectedItem 复位；
            // 再推一次（含下一帧）保证收起框落在当前会话上。
            OnPropertyChanged(nameof(SelectedChoice));
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(SelectedChoice)));
        }

        private void ReassertSelection()
        {
            var item = FindItem(_currentSession);
            if (item is null) return;

            _selectedChoice = item;
            OnPropertyChanged(nameof(SelectedChoice));
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(SelectedChoice)));
        }

        private void RefreshPortsCore()
        {
            AvailablePorts.Clear();
            foreach (var port in _hub.GetAvailablePorts())
            {
                AvailablePorts.Add(port);
            }
        }

        private void RefreshAggregate()
        {
            bool canClose = CanCloseSession;
            if (canClose != _canCloseCached)
            {
                _canCloseCached = canClose;
                OnPropertyChanged(nameof(CanCloseSession));
                CloseSessionCommand.NotifyCanExecuteChanged();
            }
            OnPropertyChanged(nameof(TotalSessions));
            OnPropertyChanged(nameof(ConnectedCount));
            OnPropertyChanged(nameof(IsAnyConnected));
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(CanAddSession));
            OnPropertyChanged(nameof(AggregateText));
        }

        private void FlushAllSessions()
        {
            foreach (var session in Sessions)
            {
                session.FlushCounters();
                session.FlushLog();
            }
            MergedLog.Flush();
        }

        private void TickSessions()
        {
            foreach (var session in Sessions)
            {
                session.UpdateElapsed();
            }
            RefreshAggregate();
        }

        private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SerialSessionViewModel.IsConnected) or nameof(SerialSessionViewModel.ConnectionStatus))
            {
                RefreshAggregate();
            }
        }

        private void OnSessionFaulted(SerialSessionViewModel session, string message)
        {
            RefreshAggregate();
            if (ReferenceEquals(session, _currentSession))
            {
                _notificationService.ShowError(message);
            }
        }

        public bool IsMergedLog
        {
            get => _isMergedLog;
            set
            {
                if (!SetProperty(ref _isMergedLog, value)) return;
                OnPropertyChanged(nameof(BufferUsageCount));
                OnPropertyChanged(nameof(BufferUsageMax));
            }
        }

        public int BufferUsageCount => _isMergedLog ? MergedLog.Count : CurrentSession?.Log.Count ?? 0;

        public int BufferUsageMax => _isMergedLog ? MergedLog.MaxLines : CurrentSession?.Log.MaxLines ?? 0;

        private void OnSessionLog(SerialSessionViewModel session, LogKind kind, string text)
        {
            MergedLog.Enqueue(kind, $"[{session.DisplayName}] {text}");
        }

        [RelayCommand]
        private void ClearLog()
        {
            if (_isMergedLog)
            {
                MergedLog.Clear();
                return;
            }
            CurrentSession?.ClearCommand.Execute(null);
        }

        [RelayCommand]
        private async Task SaveLog()
        {
            if (_isMergedLog)
            {
                await LogFileHelper.SaveAsync(MergedLog, _fileDialogService, _notificationService, "SerialMerged");
                return;
            }
            if (CurrentSession is { } session)
            {
                await session.SaveDataCommand.ExecuteAsync(null);
            }
        }

        [RelayCommand]
        private void RefreshPorts()
        {
            RefreshPortsCore();
            SyncChoiceItems();
            SelectChoice(CurrentSession, reassert: true);
        }

        [RelayCommand(CanExecute = nameof(CanCloseSession))]
        private void CloseSession()
        {
            var session = CurrentSession;
            if (session is null) return;
            if (!CanCloseSession) return;

            int index = Sessions.IndexOf(session);
            RemoveSession(session, releaseHub: true);
            SyncChoiceItems();

            if (Sessions.Count == 0)
            {
                RefreshAggregate();
                InitializeDefaultSession();
                return;
            }

            CurrentSession = Sessions[Math.Clamp(index, 0, Sessions.Count - 1)];
            SelectChoice(CurrentSession, reassert: true);
        }

        [RelayCommand]
        private async Task Send()
        {
            if (IsBroadcast)
            {
                foreach (var session in Sessions.Where(s => s.IsConnected).ToArray())
                {
                    await session.SendCurrentAsync().ConfigureAwait(false);
                }
                return;
            }

            if (CurrentSession is { } current)
            {
                await current.SendCurrentAsync().ConfigureAwait(false);
            }
        }
    }
}