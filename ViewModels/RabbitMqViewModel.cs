using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Models.ViewStates;
using LittleFancyToolAva.Services;
using LittleFancyToolAva.Utils;
using Microsoft.Extensions.Logging;

namespace LittleFancyToolAva.ViewModels
{
    public partial class RabbitMqViewModel : ViewModelBase, IDisposable, IViewState, IViewLifecycle
    {
        private readonly IRabbitMqService _mqService;
        private readonly INotificationService _notificationService;
        private readonly IViewStateService _viewStateService;
        private readonly ILogger<RabbitMqViewModel> _logger;

        private CancellationTokenSource? _connCts;
        private DispatcherTimer? _elapsedTimer;
        private DateTime? _startedAt;
        private bool _disposed;

        public ObservableCollection<string> ExchangeTypes { get; } = ["direct", "topic", "fanout", "headers"];
        public ObservableCollection<string> AckModes { get; } = ["自动 ACK", "手动 ACK"];

        public ObservableCollection<RabbitMqMessage> ReceivedMessages { get; } = new();
        public ObservableCollection<string> ExchangeList { get; } = new();
        public ObservableCollection<string> QueueList { get; } = new();

        public LogBuffer Log { get; } = new();

        public string Host
        {
            get;
            set => SetProperty(ref field, value);
        } = "127.0.0.1";

        public int Port
        {
            get;
            set => SetProperty(ref field, value);
        } = 5672;

        public string UserName
        {
            get;
            set => SetProperty(ref field, value);
        } = "guest";

        public string Password
        {
            get;
            set => SetProperty(ref field, value);
        } = "guest";

        public string VirtualHost
        {
            get;
            set => SetProperty(ref field, value);
        } = "/";

        public string ExchangeName
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public int ExchangeTypeIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        public string RoutingKey
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string HeadersJson
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string PublishExchangeText
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public bool ExchangeAutoDeclare
        {
            get;
            set => SetProperty(ref field, value);
        } = true;

        public bool ExchangeIsPersistent
        {
            get;
            set => SetProperty(ref field, value);
        }

        public string QueueName
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string PublishQueueText
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public bool QueueAutoDeclare
        {
            get;
            set => SetProperty(ref field, value);
        } = true;

        public bool QueueIsPersistent
        {
            get;
            set => SetProperty(ref field, value);
        }

        public string ConsumeQueue
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public int AckModeIndex
        {
            get;
            set => SetProperty(ref field, value);
        } = 1;

        public int PublishTabIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

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

        public bool IsConnected
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(IsAnyActive));
                    UpdateConnectionState();
                }
            }
        }

        public bool IsConsuming
        {
            get;
            set => SetProperty(ref field, value);
        }

        public bool IsAnyActive => IsConnected;

        public ConnectionStatus ConnectionStatus
        {
            get;
            set => SetProperty(ref field, value);
        } = ConnectionStatus.Idle;

        public string StatusText
        {
            get;
            set => SetProperty(ref field, value);
        } = "就绪";

        public string StatusDetail
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

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

        public int PendingAckCount
        {
            get;
            set => SetProperty(ref field, value);
        }

        public string ElapsedText
        {
            get;
            set => SetProperty(ref field, value);
        } = "00:00:00";

        public RabbitMqMessage? SelectedMessage
        {
            get;
            set => SetProperty(ref field, value);
        }

        string IViewState.ViewName => "rabbitMqView";

        public RabbitMqViewModel(
            IRabbitMqService mqService,
            INotificationService notificationService,
            IViewStateService viewStateService,
            ILogger<RabbitMqViewModel> logger)
        {
            _mqService = mqService;
            _notificationService = notificationService;
            _viewStateService = viewStateService;
            _logger = logger;
            _viewStateService.Register(this);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ((IViewLifecycle)this).OnNavigatedFrom();
            _viewStateService.Unregister(this);
        }

        void IViewLifecycle.OnNavigatedTo()
        {
            _mqService.StatusChanged += OnServiceStatusChanged;
            _mqService.MessageReceived += OnServiceMessageReceived;
            _mqService.ErrorOccurred += OnServiceErrorOccurred;
            _mqService.Disconnected += OnServiceDisconnected;
            if (IsConnected) StartElapsedTimer();
        }

        void IViewLifecycle.OnNavigatedFrom()
        {
            _mqService.StatusChanged -= OnServiceStatusChanged;
            _mqService.MessageReceived -= OnServiceMessageReceived;
            _mqService.ErrorOccurred -= OnServiceErrorOccurred;
            _mqService.Disconnected -= OnServiceDisconnected;
            _connCts?.Cancel();
            _connCts?.Dispose();
            _connCts = null;
            _ = Task.Run(async () =>
            {
                try { await _mqService.DisconnectAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Force disconnect on navigated from"); }
            });
            StopElapsedTimer();
            IsConnected = false;
            IsConsuming = false;
        }

        object IViewState.CaptureState() => new RabbitMqViewState
        {
            Host = Host,
            Port = Port,
            UserName = UserName,
            Password = string.Empty,
            VirtualHost = VirtualHost,
            ExchangeName = ExchangeName,
            ExchangeTypeIndex = ExchangeTypeIndex.ToString(),
            RoutingKey = RoutingKey,
            HeadersJson = HeadersJson,
            PublishExchangeText = PublishExchangeText,
            ExchangeAutoDeclare = ExchangeAutoDeclare,
            ExchangeIsPersistent = ExchangeIsPersistent,
            QueueName = QueueName,
            PublishQueueText = PublishQueueText,
            QueueAutoDeclare = QueueAutoDeclare,
            QueueIsPersistent = QueueIsPersistent,
            ConsumeQueue = ConsumeQueue,
            AckModeIndex = AckModeIndex,
            IsHexSend = IsHexSend,
            IsHexDisplay = IsHexDisplay
        };

        void IViewState.RestoreState(object state)
        {
            if (state is RabbitMqViewState s)
            {
                Host = s.Host;
                Port = s.Port;
                UserName = s.UserName;
                Password = string.Empty;
                VirtualHost = s.VirtualHost;
                ExchangeName = s.ExchangeName;
                if (int.TryParse(s.ExchangeTypeIndex, out var idx)) ExchangeTypeIndex = idx;
                RoutingKey = s.RoutingKey;
                HeadersJson = s.HeadersJson;
                PublishExchangeText = s.PublishExchangeText;
                ExchangeAutoDeclare = s.ExchangeAutoDeclare;
                ExchangeIsPersistent = s.ExchangeIsPersistent;
                QueueName = s.QueueName;
                PublishQueueText = s.PublishQueueText;
                QueueAutoDeclare = s.QueueAutoDeclare;
                QueueIsPersistent = s.QueueIsPersistent;
                ConsumeQueue = s.ConsumeQueue;
                AckModeIndex = s.AckModeIndex;
                IsHexSend = s.IsHexSend;
                IsHexDisplay = s.IsHexDisplay;
            }
        }

        private void OnServiceStatusChanged(string status)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = status;
                Log.Append(LogKind.System, status);
            });
        }

        private void OnServiceErrorOccurred(Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Log.Append(LogKind.Error, ex.Message);
            });
        }

        private void OnServiceDisconnected()
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = false;
                IsConsuming = false;
                ConnectionStatus = ConnectionStatus.Idle;
                StopElapsedTimer();
                Log.Append(LogKind.Warn, "连接已断开");
            });
        }

        private void OnServiceMessageReceived(RabbitMqMessage msg)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ReceivedMessages.Insert(0, msg);
                if (AckModeIndex == 1)
                {
                    PendingAckCount++;
                }
                AppendReceivedLog(msg);
                RxCount++;
            });
        }

        private void AppendReceivedLog(RabbitMqMessage msg)
        {
            string text;
            if (IsHexDisplay)
            {
                text = ToolMethod.ByteArrayToHexString(msg.Body);
            }
            else
            {
                try { text = Encoding.UTF8.GetString(msg.Body); }
                catch { text = $"<{msg.Body.Length} bytes>"; }
            }
            var headers = RabbitMqService.FormatHeaders(msg.Headers);
            Log.Append(LogKind.Rx, $"[{msg.DisplayExchange} → {msg.QueueName}] rk={msg.RoutingKey} dt={msg.DeliveryTag}{(!string.IsNullOrEmpty(headers) ? $" hdr={headers}" : "")} {text}");
        }

        private void StartElapsedTimer()
        {
            StopElapsedTimer();
            _startedAt = DateTime.Now;
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += (_, _) =>
            {
                if (_startedAt is { } start)
                {
                    ElapsedText = (DateTime.Now - start).ToString(@"hh\:mm\:ss");
                }
            };
            _elapsedTimer.Start();
        }

        private void StopElapsedTimer()
        {
            _elapsedTimer?.Stop();
            _elapsedTimer = null;
            _startedAt = null;
            ElapsedText = "00:00:00";
        }

        private void UpdateConnectionState()
        {
            if (IsConnected)
            {
                ConnectionStatus = ConnectionStatus.Connected;
                StatusDetail = $"{Host}:{Port} vhost={VirtualHost}";
                StartElapsedTimer();
            }
            else
            {
                ConnectionStatus = ConnectionStatus.Idle;
                StatusDetail = string.Empty;
                StopElapsedTimer();
            }
        }

        [RelayCommand]
        private async Task ConnectAsync()
        {
            if (IsConnected)
            {
                _notificationService.ShowWarn("已连接，请先断开。");
                return;
            }
            if (string.IsNullOrWhiteSpace(Host) || Port < 1 || Port > 65535)
            {
                _notificationService.ShowWarn("请填写有效的 Host 与端口 (1-65535)。");
                return;
            }
            if (string.IsNullOrWhiteSpace(UserName))
            {
                _notificationService.ShowWarn("请填写用户名。");
                return;
            }

            _connCts?.Cancel();
            _connCts?.Dispose();
            _connCts = new CancellationTokenSource();
            ConnectionStatus = ConnectionStatus.Connecting;
            Log.Append(LogKind.System, $"正在连接 {Host}:{Port} vhost={VirtualHost}");
            try
            {
                await _mqService.ConnectAsync(new RabbitMqConnectionConfig
                {
                    Host = Host,
                    Port = Port,
                    UserName = UserName,
                    Password = Password,
                    VirtualHost = string.IsNullOrEmpty(VirtualHost) ? "/" : VirtualHost
                }, _connCts.Token);
                IsConnected = true;
                _notificationService.ShowSuccess($"已连接 {Host}:{Port}");
            }
            catch (Exception ex)
            {
                ConnectionStatus = ConnectionStatus.Error;
                IsConnected = false;
                _notificationService.ShowError($"连接失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task DisconnectAsync()
        {
            if (!IsConnected) return;
            try
            {
                await _mqService.StopConsumeAsync();
                await _mqService.DisconnectAsync();
                IsConsuming = false;
                IsConnected = false;
                ExchangeList.Clear();
                QueueList.Clear();
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"断开失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task RefreshExchangesAsync()
        {
            if (!IsConnected)
            {
                _notificationService.ShowWarn("请先连接。");
                return;
            }
            var seeds = new List<string>
            {
                ExchangeName,
                string.Empty,
                "amq.direct", "amq.topic", "amq.fanout", "amq.headers"
            };
            seeds.AddRange(ExchangeList);
            try
            {
                var found = await _mqService.ListExchangesAsync(seeds, _connCts?.Token ?? CancellationToken.None);
                ExchangeList.Clear();
                foreach (var item in found) ExchangeList.Add(item);
                _notificationService.ShowInfo($"发现 {found.Count} 个交换机");
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"枚举交换机失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task RefreshQueuesAsync()
        {
            if (!IsConnected)
            {
                _notificationService.ShowWarn("请先连接。");
                return;
            }
            var seeds = new List<string>
            {
                QueueName,
                ConsumeQueue
            };
            seeds.AddRange(QueueList);
            try
            {
                var found = await _mqService.ListQueuesAsync(seeds, _connCts?.Token ?? CancellationToken.None);
                QueueList.Clear();
                foreach (var item in found) QueueList.Add(item);
                _notificationService.ShowInfo($"发现 {found.Count} 个队列");
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"枚举队列失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task PublishToExchangeAsync()
        {
            if (!IsConnected)
            {
                _notificationService.ShowWarn("请先连接。");
                return;
            }
            if (string.IsNullOrWhiteSpace(ExchangeName))
            {
                _notificationService.ShowWarn("请填写交换机名。");
                return;
            }
            try
            {
                byte[] body = IsHexSend
                    ? ToolMethod.HexStringToBytes(PublishExchangeText ?? string.Empty)
                    : Encoding.UTF8.GetBytes(PublishExchangeText ?? string.Empty);

                IDictionary<string, object?>? headers = null;
                if (!string.IsNullOrWhiteSpace(HeadersJson))
                {
                    try
                    {
                        headers = JsonSerializer.Deserialize<Dictionary<string, object?>>(HeadersJson);
                    }
                    catch (Exception ex)
                    {
                        _notificationService.ShowWarn($"Headers JSON 解析失败: {ex.Message}");
                        return;
                    }
                }

                await _mqService.PublishToExchangeAsync(new PublishToExchangeArgs
                {
                    Exchange = ExchangeName,
                    ExchangeType = ExchangeTypes[ExchangeTypeIndex],
                    RoutingKey = RoutingKey ?? string.Empty,
                    AutoDeclare = ExchangeAutoDeclare,
                    Persistent = ExchangeIsPersistent,
                    Body = body,
                    ContentType = IsHexSend ? "application/octet-stream" : "text/plain",
                    Headers = headers
                }, _connCts?.Token ?? CancellationToken.None);
                AppendSentLog("EX", ExchangeName, RoutingKey, body);
                TxCount++;
            }
            catch (Exception ex)
            {
                Log.Append(LogKind.Error, $"发布到交换机失败: {ex.Message}");
                _notificationService.ShowError($"发布失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task PublishToQueueAsync()
        {
            if (!IsConnected)
            {
                _notificationService.ShowWarn("请先连接。");
                return;
            }
            if (string.IsNullOrWhiteSpace(QueueName))
            {
                _notificationService.ShowWarn("请填写队列名。");
                return;
            }
            try
            {
                byte[] body = IsHexSend
                    ? ToolMethod.HexStringToBytes(PublishQueueText ?? string.Empty)
                    : Encoding.UTF8.GetBytes(PublishQueueText ?? string.Empty);

                await _mqService.PublishToQueueAsync(new PublishToQueueArgs
                {
                    QueueName = QueueName,
                    AutoDeclare = QueueAutoDeclare,
                    Persistent = QueueIsPersistent,
                    Body = body,
                    ContentType = IsHexSend ? "application/octet-stream" : "text/plain"
                }, _connCts?.Token ?? CancellationToken.None);
                AppendSentLog("Q", "", QueueName, body);
                TxCount++;
            }
            catch (Exception ex)
            {
                Log.Append(LogKind.Error, $"发布到队列失败: {ex.Message}");
                _notificationService.ShowError($"发布失败: {ex.Message}");
            }
        }

        private void AppendSentLog(string kind, string exchange, string target, byte[] body)
        {
            string text;
            if (IsHexDisplay)
            {
                text = ToolMethod.ByteArrayToHexString(body);
            }
            else
            {
                try { text = Encoding.UTF8.GetString(body); }
                catch { text = $"<{body.Length}B>"; }
            }
            if (kind == "EX")
            {
                Log.Append(LogKind.Tx, $"→ Exchange {exchange} (rk={target}) {text}");
            }
            else
            {
                Log.Append(LogKind.Tx, $"→ Queue {target} {text}");
            }
        }

        [RelayCommand]
        private async Task BasicGetAsync()
        {
            if (!IsConnected)
            {
                _notificationService.ShowWarn("请先连接。");
                return;
            }
            if (string.IsNullOrWhiteSpace(ConsumeQueue))
            {
                _notificationService.ShowWarn("请填写订阅/拉取队列名。");
                return;
            }
            try
            {
                bool autoAck = AckModeIndex == 0;
                var msg = await _mqService.BasicGetAsync(ConsumeQueue, autoAck, _connCts?.Token ?? CancellationToken.None);
                if (msg is null)
                {
                    _notificationService.ShowInfo("队列暂无可用消息");
                    return;
                }
                ReceivedMessages.Insert(0, msg);
                if (AckModeIndex == 1)
                {
                    PendingAckCount++;
                }
                AppendReceivedLog(msg);
                RxCount++;
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"BasicGet 失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task StartConsumeAsync()
        {
            if (!IsConnected)
            {
                _notificationService.ShowWarn("请先连接。");
                return;
            }
            if (IsConsuming)
            {
                _notificationService.ShowWarn("已在订阅中。");
                return;
            }
            if (string.IsNullOrWhiteSpace(ConsumeQueue))
            {
                _notificationService.ShowWarn("请填写订阅队列名。");
                return;
            }
            try
            {
                bool autoAck = AckModeIndex == 0;
                await _mqService.StartConsumeAsync(ConsumeQueue, autoAck, _connCts?.Token ?? CancellationToken.None);
                IsConsuming = true;
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"订阅失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task StopConsumeAsync()
        {
            if (!IsConsuming) return;
            try
            {
                await _mqService.StopConsumeAsync();
                IsConsuming = false;
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"停止订阅失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task AckSelectedAsync()
        {
            if (SelectedMessage is null)
            {
                _notificationService.ShowWarn("请先选择一条消息。");
                return;
            }
            try
            {
                await _mqService.AckAsync(SelectedMessage.DeliveryTag, false, _connCts?.Token ?? CancellationToken.None);
                Log.Append(LogKind.System, $"ACK dt={SelectedMessage.DeliveryTag}");
                PendingAckCount = Math.Max(0, PendingAckCount - 1);
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"ACK 失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task NackSelectedAsync()
        {
            if (SelectedMessage is null)
            {
                _notificationService.ShowWarn("请先选择一条消息。");
                return;
            }
            try
            {
                await _mqService.NackAsync(SelectedMessage.DeliveryTag, true, _connCts?.Token ?? CancellationToken.None);
                Log.Append(LogKind.System, $"NACK(requeue) dt={SelectedMessage.DeliveryTag}");
                PendingAckCount = Math.Max(0, PendingAckCount - 1);
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"NACK 失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ClearReceived()
        {
            ReceivedMessages.Clear();
            PendingAckCount = 0;
            RxCount = 0;
        }

        [RelayCommand]
        private void ClearLog()
        {
            Log.Clear();
            TxCount = 0;
        }
    }
}