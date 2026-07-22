using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LittleFancyToolAva.Models;

namespace LittleFancyToolAva.Services
{
    public interface IRabbitMqService : IDisposable
    {
        bool IsConnected { get; }

        event Action<string>? StatusChanged;
        event Action<RabbitMqMessage>? MessageReceived;
        event Action<Exception>? ErrorOccurred;
        event Action? Disconnected;

        Task ConnectAsync(RabbitMqConnectionConfig cfg, CancellationToken ct);
        Task DisconnectAsync();

        Task<IReadOnlyList<string>> ListQueuesAsync(IReadOnlyList<string> candidates, CancellationToken ct);
        Task<IReadOnlyList<string>> ListExchangesAsync(IReadOnlyList<string> candidates, CancellationToken ct);

        Task PublishToExchangeAsync(PublishToExchangeArgs args, CancellationToken ct);
        Task PublishToQueueAsync(PublishToQueueArgs args, CancellationToken ct);

        Task<RabbitMqMessage?> BasicGetAsync(string queue, bool autoAck, CancellationToken ct);
        Task StartConsumeAsync(string queue, bool autoAck, CancellationToken ct);
        Task StopConsumeAsync();

        Task AckAsync(ulong deliveryTag, bool multiple, CancellationToken ct);
        Task NackAsync(ulong deliveryTag, bool requeue, CancellationToken ct);
    }
}