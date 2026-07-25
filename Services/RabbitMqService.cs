using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LittleFancyToolAva.Models;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace LittleFancyToolAva.Services
{
    public sealed class RabbitMqService : IRabbitMqService
    {
        private readonly ILogger<RabbitMqService> _logger;
        private readonly SemaphoreSlim _connLock = new(1, 1);

        private IConnection? _connection;
        private IChannel? _publishChannel;
        private IChannel? _consumeChannel;
        private AsyncEventingBasicConsumer? _consumer;
        private string? _consumerTag;
        private bool _disposed;

        public bool IsConnected => _connection?.IsOpen == true;

        public event Action<string>? StatusChanged;
        public event Action<RabbitMqMessage>? MessageReceived;
        public event Action<Exception>? ErrorOccurred;
        public event Action? Disconnected;

        public RabbitMqService(ILogger<RabbitMqService> logger)
        {
            _logger = logger;
            _logger.LogInformation("RabbitMqService created");
        }

        public async Task ConnectAsync(RabbitMqConnectionConfig cfg, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RabbitMqService));
            await _connLock.WaitAsync(ct);
            try
            {
                await CloseAsync();
                var factory = new ConnectionFactory
                {
                    HostName = cfg.Host,
                    Port = cfg.Port,
                    UserName = cfg.UserName,
                    Password = cfg.Password,
                    VirtualHost = cfg.VirtualHost,
                    AutomaticRecoveryEnabled = true,
                    ContinuationTimeout = TimeSpan.FromSeconds(10),
                    HandshakeContinuationTimeout = TimeSpan.FromSeconds(10),
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(10),
                    SocketReadTimeout = TimeSpan.FromSeconds(10),
                    SocketWriteTimeout = TimeSpan.FromSeconds(10),
                    ClientProvidedName = "LittleFancyToolAva"
                };

                StatusChanged?.Invoke($"正在连接 {cfg.Host}:{cfg.Port} ...");

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
                _connection = await factory.CreateConnectionAsync(timeoutCts.Token);
                _publishChannel = await _connection.CreateChannelAsync(cancellationToken: timeoutCts.Token);
                _consumeChannel = await _connection.CreateChannelAsync(cancellationToken: timeoutCts.Token);

                _connection.ConnectionShutdownAsync += OnConnectionShutdown;

                _logger.LogInformation("RabbitMQ connected: {Host}:{Port} vhost={VHost}", cfg.Host, cfg.Port, cfg.VirtualHost);
                StatusChanged?.Invoke($"已连接 {_connection.Endpoint.HostName}:{_connection.Endpoint.Port} vhost={cfg.VirtualHost}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogError("RabbitMQ connect timed out after 15s");
                StatusChanged?.Invoke("连接超时 (15s)");
                ErrorOccurred?.Invoke(new TimeoutException("RabbitMQ 连接超时 (15秒)"));
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RabbitMQ connect failed");
                StatusChanged?.Invoke($"连接失败: {ex.Message}");
                ErrorOccurred?.Invoke(ex);
                if (_publishChannel != null) { try { await _publishChannel.CloseAsync(); } catch { } _publishChannel.Dispose(); _publishChannel = null; }
                if (_consumeChannel != null) { try { await _consumeChannel.CloseAsync(); } catch { } _consumeChannel.Dispose(); _consumeChannel = null; }
                if (_connection != null) { try { await _connection.CloseAsync(); } catch { } _connection.Dispose(); _connection = null; }
                throw;
            }
            finally
            {
                _connLock.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            await _connLock.WaitAsync();
            try
            {
                await CloseAsync();
                StatusChanged?.Invoke("已断开");
                _logger.LogInformation("RabbitMQ disconnected");
            }
            finally
            {
                _connLock.Release();
            }
        }

        private async Task CloseAsync()
        {
            if (_consumerTag is not null && _consumeChannel is { IsOpen: true })
            {
                try { await _consumeChannel.BasicCancelAsync(_consumerTag, false, CancellationToken.None); }
                catch (Exception ex) { _logger.LogDebug(ex, "Cancel consumer during close"); }
            }
            _consumerTag = null;
            _consumer = null;

            if (_publishChannel is not null)
            {
                try { await _publishChannel.CloseAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Close publish channel"); }
                _publishChannel.Dispose();
                _publishChannel = null;
            }
            if (_consumeChannel is not null)
            {
                try { await _consumeChannel.CloseAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Close consume channel"); }
                _consumeChannel.Dispose();
                _consumeChannel = null;
            }
            if (_connection is not null)
            {
                _connection.ConnectionShutdownAsync -= OnConnectionShutdown;
                try { await _connection.CloseAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Close connection"); }
                _connection.Dispose();
                _connection = null;
            }
        }

        private Task OnConnectionShutdown(object sender, ShutdownEventArgs e)
        {
            _logger.LogWarning("RabbitMQ connection shutdown: {ReplyCode} {ReplyText}", e.ReplyCode, e.ReplyText);
            StatusChanged?.Invoke($"连接已关闭: {e.ReplyText} ({e.ReplyCode})");
            Disconnected?.Invoke();
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<string>> ListQueuesAsync(IReadOnlyList<string> candidates, CancellationToken ct)
        {
            var results = new List<string>();
            if (_consumeChannel is null || !_consumeChannel.IsOpen)
            {
                StatusChanged?.Invoke("未连接，无法枚举队列");
                return results;
            }
            foreach (var name in candidates)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                ct.ThrowIfCancellationRequested();
                try
                {
                    await _consumeChannel.QueueDeclarePassiveAsync(name, ct);
                    results.Add(name);
                }
                catch (OperationInterruptedException) { }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Queue passive declare failed: {Queue}", name);
                }
            }
            StatusChanged?.Invoke($"枚举完成，找到 {results.Count} 个队列");
            return results;
        }

        public async Task<IReadOnlyList<string>> ListExchangesAsync(IReadOnlyList<string> candidates, CancellationToken ct)
        {
            var results = new List<string>();
            if (_publishChannel is null || !_publishChannel.IsOpen)
            {
                StatusChanged?.Invoke("未连接，无法枚举交换机");
                return results;
            }
            foreach (var name in candidates)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                ct.ThrowIfCancellationRequested();
                try
                {
                    await _publishChannel.ExchangeDeclarePassiveAsync(name, ct);
                    results.Add(name);
                }
                catch (OperationInterruptedException) { }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Exchange passive declare failed: {Exchange}", name);
                }
            }
            StatusChanged?.Invoke($"枚举完成，找到 {results.Count} 个交换机");
            return results;
        }

        public async Task PublishToExchangeAsync(PublishToExchangeArgs args, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(args.Exchange))
                throw new ArgumentException("Exchange 不能为空");
            if (args.Body?.Length > 128 * 1024 * 1024)
                throw new ArgumentException("Body 超出 128MiB 限制");

            var channel = EnsurePublishChannel();
            if (args.AutoDeclare && !string.IsNullOrEmpty(args.Exchange))
            {
                try
                {
                    await channel.ExchangeDeclareAsync(args.Exchange, args.ExchangeType, true, false, null, false, false, ct);
                }
                catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 406)
                {
                    _logger.LogError(ex, "Exchange declare conflict: {Exchange}", args.Exchange);
                    throw new InvalidOperationException($"交换机 '{args.Exchange}' 参数冲突: {ex.ShutdownReason.ReplyText}", ex);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Exchange declare failed: {Exchange}", args.Exchange);
                }
            }

            var props = new BasicProperties
            {
                ContentType = args.ContentType,
                Persistent = args.Persistent
            };
            if (args.Headers is { Count: > 0 })
            {
                props.Headers = args.Headers as Dictionary<string, object?>
                    ?? new Dictionary<string, object?>(args.Headers);
            }

            await channel.BasicPublishAsync(args.Exchange, args.RoutingKey, false, props, args.Body, ct);
            StatusChanged?.Invoke($"已发布到交换机 {args.Exchange} (rk={args.RoutingKey}) {args.Body.Length}B");
        }

        public async Task PublishToQueueAsync(PublishToQueueArgs args, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(args.QueueName))
                throw new InvalidOperationException("队列名不能为空");
            if (args.Body?.Length > 128 * 1024 * 1024)
                throw new ArgumentException("Body 超出 128MiB 限制");

            var channel = EnsurePublishChannel();
            if (args.AutoDeclare)
            {
                try
                {
                    await channel.QueueDeclareAsync(args.QueueName, true, false, false, null, false, false, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Queue declare failed: {Queue}", args.QueueName);
                }
            }

            var props = new BasicProperties
            {
                ContentType = args.ContentType,
                Persistent = args.Persistent
            };

            await channel.BasicPublishAsync(exchange: string.Empty, routingKey: args.QueueName, mandatory: false, basicProperties: props, body: args.Body, cancellationToken: ct);
            StatusChanged?.Invoke($"已发布到默认交换机 → 队列 {args.QueueName} {args.Body.Length}B");
        }

        public async Task<RabbitMqMessage?> BasicGetAsync(string queue, bool autoAck, CancellationToken ct)
        {
            var channel = EnsureConsumeChannel();
            var result = await channel.BasicGetAsync(queue, autoAck, ct);
            if (result is null)
            {
                StatusChanged?.Invoke($"BasicGet: 队列 {queue} 暂无可用消息");
                return null;
            }
            var msg = BuildMessageFromGet(result, queue);
            StatusChanged?.Invoke($"BasicGet: 取到一条消息 ({result.Body.Length}B)");
            return msg;
        }

        public async Task StartConsumeAsync(string queue, bool autoAck, CancellationToken ct)
        {
            var channel = EnsureConsumeChannel();
            if (_consumerTag is not null)
            {
                await StopConsumeAsync();
            }
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += OnConsumerReceived;
            _consumer = consumer;
            _consumerTag = await channel.BasicConsumeAsync(queue, autoAck, consumer, ct);
            StatusChanged?.Invoke($"开始订阅队列 {queue} (autoAck={autoAck}, tag={_consumerTag})");
        }

        public async Task StopConsumeAsync()
        {
            if (_consumerTag is null) return;
            if (_consumeChannel is { IsOpen: true })
            {
                try { await _consumeChannel.BasicCancelAsync(_consumerTag, false, CancellationToken.None); }
                catch (Exception ex) { _logger.LogDebug(ex, "Cancel consumer"); }
            }
            if (_consumer is not null)
            {
                _consumer.ReceivedAsync -= OnConsumerReceived;
            }
            _consumer = null;
            _consumerTag = null;
            StatusChanged?.Invoke("已停止订阅");
        }

        public Task AckAsync(ulong deliveryTag, bool multiple, CancellationToken ct)
        {
            var channel = EnsureConsumeChannel();
            return channel.BasicAckAsync(deliveryTag, multiple, ct).AsTask();
        }

        public Task NackAsync(ulong deliveryTag, bool requeue, CancellationToken ct)
        {
            var channel = EnsureConsumeChannel();
            return channel.BasicNackAsync(deliveryTag, false, requeue, ct).AsTask();
        }

        private Task OnConsumerReceived(object sender, BasicDeliverEventArgs ea)
        {
            try
            {
                var msg = BuildMessageFromDeliver(ea);
                MessageReceived?.Invoke(msg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Consumer receive error");
            }
            return Task.CompletedTask;
        }

        private static RabbitMqMessage BuildMessageFromGet(BasicGetResult r, string queue)
        {
            return new RabbitMqMessage
            {
                DeliveryTag = r.DeliveryTag,
                Exchange = r.Exchange ?? string.Empty,
                RoutingKey = r.RoutingKey ?? string.Empty,
                QueueName = queue,
                ContentType = r.BasicProperties?.ContentType ?? "text/plain",
                Headers = r.BasicProperties?.Headers,
                Body = r.Body.ToArray(),
                Redelivered = r.Redelivered,
                Timestamp = DateTime.Now
            };
        }

        private static RabbitMqMessage BuildMessageFromDeliver(BasicDeliverEventArgs e)
        {
            return new RabbitMqMessage
            {
                DeliveryTag = e.DeliveryTag,
                Exchange = e.Exchange ?? string.Empty,
                RoutingKey = e.RoutingKey ?? string.Empty,
                ContentType = e.BasicProperties?.ContentType ?? "text/plain",
                Headers = e.BasicProperties?.Headers,
                Body = e.Body.ToArray(),
                Redelivered = e.Redelivered,
                Timestamp = DateTime.Now
            };
        }

        private IChannel EnsurePublishChannel()
        {
            if (_publishChannel is null || !_publishChannel.IsOpen)
                throw new InvalidOperationException("未连接到 RabbitMQ");
            return _publishChannel;
        }

        private IChannel EnsureConsumeChannel()
        {
            if (_consumeChannel is null || !_consumeChannel.IsOpen)
                throw new InvalidOperationException("未连接到 RabbitMQ");
            return _consumeChannel;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { await DisconnectAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Disconnect during dispose"); }
            _connLock.Dispose();
            _logger.LogInformation("RabbitMqService disposed");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dispose timeout or error");
            }
            _connLock.Dispose();
            GC.SuppressFinalize(this);
        }

        public static string FormatHeaders(IDictionary<string, object?>? headers)
        {
            if (headers is null || headers.Count == 0) return string.Empty;
            try
            {
                Dictionary<string, object?> concrete = headers as Dictionary<string, object?>
                    ?? new Dictionary<string, object?>(headers);
                return JsonSerializer.Serialize(concrete, RabbitMqJsonContext.Default.DictionaryStringObject);
            }
            catch
            {
                var sb = new StringBuilder();
                foreach (var kv in headers)
                {
                    sb.Append(kv.Key).Append('=');
                    if (kv.Value is byte[] b)
                        sb.Append($"<{b.Length}B>");
                    else
                        sb.Append(kv.Value);
                    sb.Append(';');
                }
                return sb.ToString();
            }
        }
    }
}
