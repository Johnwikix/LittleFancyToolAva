using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FancyToolAva.Utils;

namespace FancyToolAva.ViewModels
{
    /// <summary>
    /// 终端类会话（串口 / TCP / UDP）中与协议无关的部分：日志、计数、未读、计时、发送选项和定时发送。
    /// 定时发送是会话级的：每一路各自一个任务和取消令牌，与页面当前显示哪一路无关。
    /// </summary>
    public abstract partial class TerminalSessionViewModelBase : ViewModelBase
    {
        private const int ActiveLogMaxLines = 20_000;
        private const int InactiveLogMaxLines = 5_000;
        private const int MaxUnreadCount = 999;

        protected readonly INotificationService _notificationService;
        private CancellationTokenSource? _pollCts;
        private DateTime? _connectedAt;
        private long _pendingRxCount;
        private long _pendingTxCount;
        private long _pendingUnread;
        private bool _isCurrent;

        protected TerminalSessionViewModelBase(INotificationService notificationService)
        {
            _notificationService = notificationService;
            Log.MaxLines = InactiveLogMaxLines;
            Log.EntryEnqueued += (kind, text) => LogSink?.Invoke(this, kind, text);
        }

        public string Id { get; private set; } = Guid.NewGuid().ToString("N");

        /// <summary>恢复存档里的会话标识，页面靠它对上"上次选中的会话"；存档没有有效标识时保留新建的。</summary>
        protected void RestoreId(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id)) Id = id;
        }

        /// <summary>可以收发的状态：串口为已连接，TCP 为监听中或已连接，UDP 为已启动。</summary>
        public abstract bool IsActive { get; }

        /// <summary>下拉项与合并日志行前缀使用的名称。</summary>
        public abstract string DisplayName { get; }

        /// <summary>启动定时发送但会话不活跃时的提示键。</summary>
        protected abstract string NotActiveMessageKey { get; }

        /// <summary>定时发送出错的日志键，参数为异常消息。</summary>
        protected abstract string TimedSendErrorMessageKey { get; }

        /// <summary>把收发字节解码为日志文本。</summary>
        protected abstract string DecodeBytes(byte[] bytes);

        /// <summary>手动发送一帧。</summary>
        public abstract Task SendManualAsync();

        /// <summary>
        /// 定时发送每一拍的发送动作。开始定时发送时在 UI 线程上调用一次，
        /// 发送目标（编码、所选客户端等）在这里取快照，之后修改界面不影响进行中的定时发送。
        /// </summary>
        protected abstract Func<Task> CreatePollingSender();

        /// <summary>需要提示用户的错误；由页面决定是否弹窗（只有当前会话才弹）。</summary>
        public event Action<TerminalSessionViewModelBase, string>? Faulted;

        protected void RaiseFault(string message)
        {
            Faulted?.Invoke(this, message);
        }

        public Action<TerminalSessionViewModelBase, LogKind, string>? LogSink { get; set; }

        public LogBuffer Log { get; } = new(autoFlush: false);

        public string SendText
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string StatusText
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string StatusDetail
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public bool IsHexSend
        {
            get;
            set => SetProperty(ref field, value);
        }

        public bool IsHexDisplay
        {
            get;
            set => SetProperty(ref field, value);
        }

        public bool IsPolling
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int PollInterval
        {
            get;
            set => SetProperty(ref field, value);
        } = 1000;

        public ConnectionStatus ConnectionStatus
        {
            get;
            set => SetProperty(ref field, value);
        } = ConnectionStatus.Idle;

        public int RxCount
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int TxCount
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int UnreadCount
        {
            get;
            private set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(HasUnread));
                    OnPropertyChanged(nameof(UnreadText));
                }
            }
        }

        public bool HasUnread => UnreadCount > 0;

        public string UnreadText => UnreadCount > 99 ? "99+" : UnreadCount.ToString();

        public string ElapsedText
        {
            get;
            set => SetProperty(ref field, value);
        } = "00:00:00";

        public bool EnableSendCount
        {
            get;
            set => SetProperty(ref field, value);
        }

        public int SendCount
        {
            get;
            set => SetProperty(ref field, value);
        } = 1;

        public string StateText => ConnectionStatus switch
        {
            ConnectionStatus.Connected => LocalizationRegistry.Get("Common.Status_Connected"),
            ConnectionStatus.Connecting => LocalizationRegistry.Get("Common.Status_Connecting"),
            ConnectionStatus.Error => LocalizationRegistry.Get("Common.Status_Error"),
            _ => LocalizationRegistry.Get("Common.Status_Idle")
        };

        /// <summary>切为当前会话时清空未读，连同接收线程已记下、尚未发布的部分。</summary>
        public void MarkRead()
        {
            Interlocked.Exchange(ref _pendingUnread, 0);
            UnreadCount = 0;
        }

        /// <summary>当前会话放大日志上限，非当前会话降级，控制多路内存占用。</summary>
        public void SetActive(bool active)
        {
            _isCurrent = active;
            Log.MaxLines = active ? ActiveLogMaxLines : InactiveLogMaxLines;
        }

        /// <summary>语言切换后重发依赖文案的通知。</summary>
        public virtual void RefreshCulture()
        {
            OnPropertyChanged(nameof(StateText));
        }

        /// <summary>由页面节拍在 UI 线程上调用：把累计的收发计数和未读一次性发布。</summary>
        internal void FlushCounters()
        {
            int tx = (int)Interlocked.Exchange(ref _pendingTxCount, 0);
            int rx = (int)Interlocked.Exchange(ref _pendingRxCount, 0);
            if (tx > 0) TxCount += tx;
            if (rx > 0) RxCount += rx;

            long pendingUnread = Interlocked.Exchange(ref _pendingUnread, 0);
            // 切到本会话的瞬间，接收线程可能仍按"非当前"记了一笔；当前会话的未读没有意义，直接丢弃。
            if (pendingUnread > 0 && !_isCurrent)
            {
                UnreadCount = (int)Math.Min(UnreadCount + pendingUnread, MaxUnreadCount);
            }
        }

        internal void FlushLog() => Log.Flush();

        internal void UpdateElapsed()
        {
            if (_connectedAt is { } start)
            {
                ElapsedText = (DateTime.Now - start).ToString(@"hh\:mm\:ss");
            }
        }

        protected void StartElapsed()
        {
            _connectedAt = DateTime.Now;
            ElapsedText = "00:00:00";
        }

        protected void StopElapsed()
        {
            _connectedAt = null;
            ElapsedText = "00:00:00";
        }

        /// <summary>收到一帧：计数、按 HEX 或文本写日志；非当前会话的未读先记账，由页面节拍在 UI 线程发布。</summary>
        protected void OnReceived(byte[] bytes)
        {
            Interlocked.Increment(ref _pendingRxCount);
            AppendFrame(LogKind.Rx, bytes);

            if (!_isCurrent) Interlocked.Increment(ref _pendingUnread);
        }

        /// <summary>发出一帧：计数并按 HEX 或文本写日志。</summary>
        protected void OnSent(byte[] bytes)
        {
            Interlocked.Increment(ref _pendingTxCount);
            AppendFrame(LogKind.Tx, bytes);
        }

        private void AppendFrame(LogKind kind, byte[] bytes)
        {
            if (IsHexDisplay)
            {
                Log.Enqueue(kind, ToolMethod.ByteArrayToSpacedHexString(bytes));
            }
            else
            {
                Log.EnqueueLine(kind, DecodeBytes(bytes));
            }
        }

        /// <summary>服务线程上产生的状态文案切回 UI 线程再发布。</summary>
        protected void PostStatusText(string status)
        {
            Dispatcher.UIThread.Post(() => StatusText = status);
        }

        protected void CancelPolling() => _pollCts?.Cancel();

        [RelayCommand]
        private void Clear()
        {
            Log.Clear();
            Interlocked.Exchange(ref _pendingRxCount, 0);
            Interlocked.Exchange(ref _pendingTxCount, 0);
            RxCount = 0;
            TxCount = 0;
        }

        [RelayCommand]
        private void StartPolling()
        {
            if (!IsActive)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get(NotActiveMessageKey));
                return;
            }
            if (IsPolling) return;

            Func<Task> send = CreatePollingSender();
            var localCts = new CancellationTokenSource();
            _pollCts = localCts;
            IsPolling = true;

            _ = Task.Run(() => PollAsync(send, localCts));
        }

        [RelayCommand]
        private void StopPolling()
        {
            _pollCts?.Cancel();
        }

        private async Task PollAsync(Func<Task> send, CancellationTokenSource localCts)
        {
            int sentCount = 0;
            long intervalTicks = Math.Max(1L, (long)(PollInterval * (Stopwatch.Frequency / 1000.0)));
            long nextDue = Stopwatch.GetTimestamp() + intervalTicks;
            try
            {
                while (!localCts.Token.IsCancellationRequested)
                {
                    if (!IsActive) break;
                    try
                    {
                        await send().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log.Enqueue(LogKind.Error, LocalizationRegistry.Get(TimedSendErrorMessageKey, ex.Message));
                    }
                    if (EnableSendCount && ++sentCount >= SendCount) break;

                    nextDue += intervalTicks;
                    long remainingTicks = nextDue - Stopwatch.GetTimestamp();
                    while (remainingTicks > 0)
                    {
                        if (localCts.Token.IsCancellationRequested) break;
                        long remainingMs = remainingTicks * 1000 / Stopwatch.Frequency;
                        if (remainingMs >= 1)
                            await Task.Delay((int)remainingMs, localCts.Token).ConfigureAwait(false);
                        else
                            await Task.Yield();
                        remainingTicks = nextDue - Stopwatch.GetTimestamp();
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_pollCts, localCts))
                    {
                        IsPolling = false;
                    }
                });
            }
        }

        /// <summary>关闭会话前的收尾：停止定时发送；派生类在这里解绑服务事件。</summary>
        public virtual void Shutdown()
        {
            _pollCts?.Cancel();
        }
    }
}
