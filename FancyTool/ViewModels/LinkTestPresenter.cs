using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lang.Avalonia;
using FancyToolAva.Services;

namespace FancyToolAva.ViewModels
{
    /// <summary>
    /// 链路测试的界面呈现状态：订阅 <see cref="TcpLinkTestService"/> /
    /// <see cref="UdpLinkTestService"/> 的后台事件，统一切换到 UI 线程后更新绑定属性。
    /// 供 TCP / UDP 两个调试页面以「链路测试」独立模式复用，本身不持有 Socket。
    /// </summary>
    public partial class LinkTestPresenter : ObservableObject
    {
        private readonly List<string> _resultLines = [];
        private const int MaxResultLines = 30;
        private int _durationSec = 10;
        private bool _failedThisRun;

        public IRelayCommand ClearResultsCommand { get; }

        /// <summary>当前是否为发送端（决定进度条与包计数字段的表现）。</summary>
        public bool IsSender
        {
            get;
            private set => SetProperty(ref field, value);
        }

        /// <summary>是否展示丢包统计字段（仅 UDP）。</summary>
        public bool HasLossStats
        {
            get;
            private set => SetProperty(ref field, value);
        }

        /// <summary>测试是否进行中。</summary>
        public bool IsRunning
        {
            get;
            private set => SetProperty(ref field, value);
        }

        public string CurrentSpeedText
        {
            get;
            private set => SetProperty(ref field, value);
        } = "—";

        public string AvgSpeedText
        {
            get;
            private set => SetProperty(ref field, value);
        } = "—";

        public string TotalDataText
        {
            get;
            private set => SetProperty(ref field, value);
        } = "—";

        public string ElapsedText
        {
            get;
            private set => SetProperty(ref field, value);
        } = "00:00:00";

        /// <summary>进度 0..100；接收端时长未知时为 -1（界面隐藏进度条）。</summary>
        public double ProgressValue
        {
            get;
            private set => SetProperty(ref field, value);
        } = -1;

        public string SentPacketsText
        {
            get;
            private set => SetProperty(ref field, value);
        } = "—";

        public string ReceivedPacketsText
        {
            get;
            private set => SetProperty(ref field, value);
        } = "—";

        public string LostPacketsText
        {
            get;
            private set => SetProperty(ref field, value);
        } = "—";

        public string LossRateText
        {
            get;
            private set => SetProperty(ref field, value);
        } = "—";

        public string StatusText
        {
            get;
            private set => SetProperty(ref field, value);
        } = "";

        /// <summary>历史测试结果（每次一行，最多保留 30 条）。</summary>
        public string ResultLog
        {
            get;
            private set => SetProperty(ref field, value);
        } = string.Empty;

        public LinkTestPresenter(TcpLinkTestService service)
        {
            ClearResultsCommand = new RelayCommand(ClearResults);
            service.PhaseChanged += OnPhaseChanged;
            service.SnapshotUpdated += OnSnapshotUpdated;
            service.Completed += OnCompleted;
            service.Failed += OnFailed;
        }

        public LinkTestPresenter(UdpLinkTestService service)
        {
            ClearResultsCommand = new RelayCommand(ClearResults);
            service.PhaseChanged += OnPhaseChanged;
            service.SnapshotUpdated += OnSnapshotUpdated;
            service.Completed += OnCompleted;
            service.Failed += OnFailed;
        }

        /// <summary>调用方捕获到启动异常（如端口绑定失败）时复位状态。</summary>
        public void Fail(string message)
        {
            IsRunning = false;
            StatusText = LocalizationRegistry.Get("LinkTest.Status_Error", message);
        }

        /// <summary>开始一次测试：重置显示并记录模式。</summary>
        public void Begin(bool isSender, int durationSec, bool hasLossStats)
        {
            IsSender = isSender;
            HasLossStats = hasLossStats;
            _durationSec = Math.Max(1, durationSec);
            _failedThisRun = false;
            IsRunning = true;
            CurrentSpeedText = "—";
            AvgSpeedText = "—";
            TotalDataText = "—";
            ElapsedText = isSender ? $"00:00:00 / {FormatClock(TimeSpan.FromSeconds(_durationSec))}" : "00:00:00";
            ProgressValue = isSender ? 0 : -1;
            SentPacketsText = isSender ? "0" : "—";
            ReceivedPacketsText = isSender ? "—" : "0";
            LostPacketsText = "—";
            LossRateText = "—";
            StatusText = LocalizationRegistry.Get(isSender
                ? "LinkTest.Status_SenderReady"
                : "LinkTest.Status_ReceiverReady");
        }

        private void OnPhaseChanged(LinkTestPhase phase)
        {
            Post(() =>
            {
                if (!IsRunning) return;
                StatusText = phase switch
                {
                    LinkTestPhase.WaitingPeer => LocalizationRegistry.Get("LinkTest.Status_WaitPeer"),
                    LinkTestPhase.Connecting => LocalizationRegistry.Get("LinkTest.Status_Connecting"),
                    LinkTestPhase.Transferring => LocalizationRegistry.Get(IsSender ? "LinkTest.Status_Sending" : "LinkTest.Status_Receiving"),
                    LinkTestPhase.Finishing => LocalizationRegistry.Get("LinkTest.Status_Finishing"),
                    _ => StatusText,
                };
            });
        }

        private void OnSnapshotUpdated(LinkTestSnapshot snapshot)
        {
            Post(() =>
            {
                if (!IsRunning) return;
                CurrentSpeedText = FormatBandwidth(snapshot.CurrentBps);
                AvgSpeedText = FormatBandwidth(snapshot.AvgBps);
                TotalDataText = FormatDataSize(snapshot.TotalBytes);
                ElapsedText = IsSender
                    ? $"{FormatClock(TimeSpan.FromSeconds(snapshot.ElapsedSec))} / {FormatClock(TimeSpan.FromSeconds(_durationSec))}"
                    : FormatClock(TimeSpan.FromSeconds(snapshot.ElapsedSec));
                if (IsSender && snapshot.ProgressRatio >= 0)
                {
                    ProgressValue = Math.Clamp(snapshot.ProgressRatio, 0, 1) * 100;
                }
                if (HasLossStats)
                {
                    if (IsSender) SentPacketsText = FormatCount(snapshot.TotalPackets);
                    else ReceivedPacketsText = FormatCount(snapshot.TotalPackets);
                }
            });
        }

        private void OnCompleted(LinkTestSummary summary)
        {
            Post(() =>
            {
                IsRunning = false;
                if (summary.TotalBytes <= 0)
                {
                    // 无数据的空汇总（如等待连接时停止）：不产生结果行，
                    // 且保留 Failed 已给出的错误/提示状态。
                    if (!_failedThisRun)
                    {
                        StatusText = LocalizationRegistry.Get("LinkTest.Status_Aborted");
                    }
                    return;
                }
                AvgSpeedText = FormatBandwidth(summary.AvgBps);
                TotalDataText = FormatDataSize(summary.TotalBytes);
                ElapsedText = FormatClock(TimeSpan.FromSeconds(summary.ElapsedSec));
                if (IsSender && summary.Completed)
                {
                    ProgressValue = 100;
                }

                if (HasLossStats)
                {
                    if (summary.IsSender)
                    {
                        SentPacketsText = FormatCount(summary.TotalPackets);
                        if (summary.HasRemoteStats && summary.LostPackets >= 0)
                        {
                            ReceivedPacketsText = FormatCount(summary.TotalPackets - summary.LostPackets);
                            LostPacketsText = FormatCount(summary.LostPackets);
                            LossRateText = FormatLossRate(summary.LossRate);
                        }
                        else
                        {
                            ReceivedPacketsText = "—";
                            LostPacketsText = "—";
                            LossRateText = "—";
                        }
                    }
                    else
                    {
                        ReceivedPacketsText = FormatCount(summary.TotalPackets);
                        if (summary.ExpectedPackets >= 0)
                        {
                            SentPacketsText = FormatCount(summary.ExpectedPackets);
                            LostPacketsText = FormatCount(Math.Max(0, summary.LostPackets));
                            LossRateText = FormatLossRate(summary.LossRate);
                        }
                        else
                        {
                            SentPacketsText = FormatCount(summary.TotalPackets + Math.Max(0, summary.LostPackets));
                        }
                    }
                }

                StatusText = LocalizationRegistry.Get(summary.Completed
                    ? "LinkTest.Status_Done"
                    : "LinkTest.Status_Aborted");
                AppendResultLine(summary);
            });
        }

        private void OnFailed(string message)
        {
            Post(() =>
            {
                IsRunning = false;
                _failedThisRun = true;
                StatusText = LocalizationRegistry.Get("LinkTest.Status_Error", message);
            });
        }

        private void AppendResultLine(LinkTestSummary summary)
        {
            string time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            string tag = LocalizationRegistry.Get(summary.Completed
                ? (summary.IsSender ? "LinkTest.Result_SendDone" : "LinkTest.Result_RecvDone")
                : "LinkTest.Result_Aborted");
            string elapsed = summary.ElapsedSec.ToString("0.0#", CultureInfo.CurrentCulture);
            string data = FormatDataSize(summary.TotalBytes);
            string avg = FormatBandwidth(summary.AvgBps);

            string line;
            if (HasLossStats)
            {
                string loss;
                if (summary.IsSender)
                {
                    if (summary.HasRemoteStats && summary.LostPackets >= 0)
                    {
                        loss = LocalizationRegistry.Get("LinkTest.Result_LossPeer",
                            summary.TotalPackets - summary.LostPackets,
                            summary.LostPackets,
                            FormatLossRate(summary.LossRate));
                    }
                    else
                    {
                        loss = LocalizationRegistry.Get("LinkTest.Result_LossPeerMissing");
                    }
                    line = LocalizationRegistry.Get("LinkTest.Result_UdpSenderLine",
                        elapsed, FormatCount(summary.TotalPackets), avg, loss);
                }
                else if (summary.ExpectedPackets >= 0)
                {
                    loss = LocalizationRegistry.Get("LinkTest.Result_LossSelf",
                        summary.ExpectedPackets,
                        Math.Max(0, summary.LostPackets),
                        FormatLossRate(summary.LossRate));
                    line = LocalizationRegistry.Get("LinkTest.Result_UdpReceiverLine",
                        elapsed, FormatCount(summary.TotalPackets), avg, loss);
                }
                else
                {
                    line = LocalizationRegistry.Get("LinkTest.Result_UdpReceiverLine",
                        elapsed, FormatCount(summary.TotalPackets), avg,
                        LocalizationRegistry.Get("LinkTest.Result_LossPeerMissing"));
                }
            }
            else
            {
                line = LocalizationRegistry.Get("LinkTest.Result_TcpLine", elapsed, data, avg);
            }

            _resultLines.Add($"[{time}] {tag} {line}");
            while (_resultLines.Count > MaxResultLines)
            {
                _resultLines.RemoveAt(0);
            }
            ResultLog = string.Join(Environment.NewLine, _resultLines);
        }

        private void ClearResults()
        {
            _resultLines.Clear();
            ResultLog = string.Empty;
        }

        /// <summary>后台事件统一切换到 UI 线程；退出阶段 Dispatcher 已停时安静忽略。</summary>
        private static void Post(Action action)
        {
            try
            {
                Dispatcher.UIThread.Post(action);
            }
            catch (InvalidOperationException)
            {
                // 应用关闭阶段无法投递：丢弃本次 UI 更新
            }
        }

        // ============ 格式化（数字与单位不属于界面文案，不做本地化） ============

        public static string FormatBandwidth(double bytesPerSec)
        {
            double bits = bytesPerSec * 8;
            if (bits >= 1e9) return string.Create(CultureInfo.CurrentCulture, $"{bits / 1e9:0.00} Gbps");
            if (bits >= 1e6) return string.Create(CultureInfo.CurrentCulture, $"{bits / 1e6:0.00} Mbps");
            if (bits >= 1e3) return string.Create(CultureInfo.CurrentCulture, $"{bits / 1e3:0.00} Kbps");
            return string.Create(CultureInfo.CurrentCulture, $"{bits:0} bps");
        }

        public static string FormatDataSize(long bytes)
        {
            if (bytes >= 1L << 30) return string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 30):0.00} GB");
            if (bytes >= 1L << 20) return string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 20):0.00} MB");
            if (bytes >= 1L << 10) return string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 10):0.00} KB");
            return string.Create(CultureInfo.CurrentCulture, $"{bytes} B");
        }

        public static string FormatCount(long value) =>
            value.ToString("N0", CultureInfo.CurrentCulture);

        public static string FormatLossRate(double rate) =>
            rate < 0 ? "—" : string.Create(CultureInfo.CurrentCulture, $"{rate * 100:0.###}%");

        public static string FormatClock(TimeSpan t) => t.ToString(@"hh\:mm\:ss");
    }
}
