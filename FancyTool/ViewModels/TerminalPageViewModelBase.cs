using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FancyToolAva.Utils;
using Lang.Avalonia;

namespace FancyToolAva.ViewModels
{
    /// <summary>
    /// 终端类调试页面的公共部分：会话集合、会话选择下拉、合并日志、聚合状态和页面级命令。
    /// 页面一次只显示一路；切换只改变显示对象，其它会话的收发与定时发送不受影响。
    /// </summary>
    /// <typeparam name="TSession">页面管理的会话类型。</typeparam>
    public abstract partial class TerminalPageViewModelBase<TSession> : ViewModelBase
        where TSession : TerminalSessionViewModelBase
    {
        private const int MergedLogMaxLines = 20_000;
        private const string MergedLogKeyPrefix = "MergedLog";

        private readonly TerminalTicker _ticker;
        private readonly Dictionary<TSession, SessionChoiceItem> _choiceBySession = [];
        private bool _suppressChoice;
        private SessionChoiceItem? _selectedChoice;
        private TSession? _currentSession;
        private bool _sendToAll;
        private bool _isMergedLog;
        private bool _canCloseCached = true;

        protected readonly INotificationService _notificationService;
        protected readonly IFileDialogService _fileDialogService;

        protected TerminalPageViewModelBase(TerminalTicker ticker, INotificationService notificationService,
            IFileDialogService fileDialogService)
        {
            _ticker = ticker;
            _notificationService = notificationService;
            _fileDialogService = fileDialogService;

            MergedLog.MaxLines = MergedLogMaxLines;

            _ticker.FastTick += FlushAllSessions;
            _ticker.SlowTick += TickSessions;
            I18nManager.Instance.CultureChanged += OnCultureChanged;
        }

        /// <summary>聚合状态文案，例如"已连接 1 / 3 会话"。</summary>
        public abstract string AggregateText { get; }

        /// <summary>没有存档时创建页面的首个会话。</summary>
        protected abstract void InitializeDefaultSession();

        /// <summary>选中下拉里的新建项时创建会话；<paramref name="payload"/> 为新建项携带的默认值。</summary>
        protected abstract TSession? CreateSession(string? payload);

        /// <summary>关闭会话并释放它占用的资源，最后把它移出 <see cref="Sessions"/>。</summary>
        protected abstract void RemoveSession(TSession session);

        /// <summary>同步下拉末尾的"新建"项：会话项在前，新建项在后。</summary>
        protected abstract void SyncAddItems();

        /// <summary>保存当前会话的日志。</summary>
        protected abstract Task SaveCurrentSessionLogAsync();

        public ObservableCollection<TSession> Sessions { get; } = [];

        public ObservableCollection<SessionChoiceItem> Choices { get; } = [];

        public LogBuffer MergedLog { get; } = new(autoFlush: false);

        public TSession? CurrentSession
        {
            get => _currentSession;
            protected set
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

        public SessionChoiceItem? SelectedChoice
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
                    CurrentSession = (TSession)session;
                    return;
                }

                // 这里正处在 ComboBox 选中变更的回调中：现在改下拉集合会让控件重入并记错上一次选中项，下一次选"新建"不再通知，推迟到回调之后。
                Dispatcher.UIThread.Post(() => AddSessionFromChoice(value));
            }
        }

        private void AddSessionFromChoice(SessionChoiceItem choice)
        {
            // 推迟期间已切到别的会话则放弃新建。
            if (!ReferenceEquals(_selectedChoice, choice)) return;

            var created = CreateSession(choice.AddPayload);
            SyncChoiceItems();
            if (created is not null)
            {
                SelectChoice(created, reassert: true);
            }
        }

        /// <summary>勾选后发送给全部活跃会话，否则只发给当前会话。</summary>
        public bool SendToAll
        {
            get => _sendToAll;
            set
            {
                if (SetProperty(ref _sendToAll, value))
                {
                    OnPropertyChanged(nameof(CanSend));
                }
            }
        }

        public int TotalSessions => Sessions.Count;

        public int ActiveCount => Sessions.Count(s => s.IsActive);

        public bool IsAnyActive => Sessions.Any(s => s.IsActive);

        public bool CanSend => SendToAll ? IsAnyActive : CurrentSession?.IsActive == true;

        public bool CanCloseSession => Sessions.Count > 1;

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

        /// <summary>把会话加入页面：订阅它的状态与日志，按需切换为当前会话。</summary>
        protected void RegisterSession(TSession session, bool select = true)
        {
            session.Faulted += OnSessionFaulted;
            session.PropertyChanged += OnSessionPropertyChanged;
            session.LogSink = OnSessionLog;
            Sessions.Add(session);
            if (select)
            {
                CurrentSession = session;
            }
            RefreshAggregate();
        }

        /// <summary>解绑会话的事件并停止它的后台任务，供 <see cref="RemoveSession"/> 使用。</summary>
        protected void DetachSession(TSession session)
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
            session.Faulted -= OnSessionFaulted;
            session.Shutdown();
        }

        /// <summary>
        /// 增量同步下拉项：不整表 Clear（Clear 会让 ComboBox 把 SelectedItem 置空，
        /// 且新实例不在旧集合里，收起框会停在"未选中"状态）。
        /// </summary>
        protected void SyncChoiceItems()
        {
            _suppressChoice = true;
            try
            {
                foreach (var pair in _choiceBySession.ToArray())
                {
                    if (Sessions.Contains(pair.Key)) continue;
                    Choices.Remove(pair.Value);
                    pair.Value.Detach();
                    _choiceBySession.Remove(pair.Key);
                }

                foreach (var session in Sessions)
                {
                    if (_choiceBySession.ContainsKey(session)) continue;
                    var item = SessionChoiceItem.ForSession(session);
                    _choiceBySession[session] = item;
                    int addIndex = IndexOfFirstAddItem();
                    if (addIndex >= 0) Choices.Insert(addIndex, item);
                    else Choices.Add(item);
                }

                SyncAddItems();
            }
            finally
            {
                _suppressChoice = false;
            }
        }

        private int IndexOfFirstAddItem()
        {
            for (int i = 0; i < Choices.Count; i++)
            {
                if (Choices[i].IsAddItem) return i;
            }
            return -1;
        }

        private SessionChoiceItem? FindItem(TSession? session)
        {
            if (session is null) return null;
            return _choiceBySession.TryGetValue(session, out var item) ? item : null;
        }

        protected void SelectChoice(TSession? session, bool reassert = false)
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

        protected void RefreshAggregate()
        {
            bool canClose = CanCloseSession;
            if (canClose != _canCloseCached)
            {
                _canCloseCached = canClose;
                OnPropertyChanged(nameof(CanCloseSession));
                CloseSessionCommand.NotifyCanExecuteChanged();
            }
            OnPropertyChanged(nameof(TotalSessions));
            OnPropertyChanged(nameof(ActiveCount));
            OnPropertyChanged(nameof(IsAnyActive));
            OnPropertyChanged(nameof(CanSend));
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
            if (e.PropertyName is nameof(TerminalSessionViewModelBase.IsActive) or nameof(TerminalSessionViewModelBase.ConnectionStatus))
            {
                RefreshAggregate();
            }
        }

        private void OnSessionFaulted(TerminalSessionViewModelBase session, string message)
        {
            // 回调可能来自线程池线程，通知回 UI 线程发布；是否弹提示按触发时刻是否为当前会话决定。
            bool isCurrent = ReferenceEquals(session, _currentSession);
            if (Dispatcher.UIThread.CheckAccess())
            {
                PublishFault(isCurrent, message);
                return;
            }

            Dispatcher.UIThread.Post(() => PublishFault(isCurrent, message));
        }

        private void PublishFault(bool isCurrent, string message)
        {
            RefreshAggregate();
            if (isCurrent)
            {
                _notificationService.ShowError(message);
            }
        }

        private void OnSessionLog(TerminalSessionViewModelBase session, LogKind kind, string text)
        {
            MergedLog.Enqueue(kind, $"[{session.DisplayName}] {text}");
        }

        private void OnCultureChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(AggregateText));
            foreach (var session in Sessions)
            {
                session.RefreshCulture();
            }
            foreach (var choice in Choices)
            {
                choice.Refresh();
            }
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
                await LogFileHelper.SaveAsync(MergedLog, _fileDialogService, _notificationService, MergedLogKeyPrefix);
                return;
            }
            await SaveCurrentSessionLogAsync();
        }

        [RelayCommand(CanExecute = nameof(CanCloseSession))]
        private void CloseSession()
        {
            var session = CurrentSession;
            if (session is null) return;
            if (!CanCloseSession) return;

            int index = Sessions.IndexOf(session);
            RemoveSession(session);
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
            if (SendToAll)
            {
                foreach (var session in Sessions.Where(s => s.IsActive).ToArray())
                {
                    await session.SendManualAsync().ConfigureAwait(false);
                }
                return;
            }

            if (CurrentSession is { } current)
            {
                await current.SendManualAsync().ConfigureAwait(false);
            }
        }
    }
}
