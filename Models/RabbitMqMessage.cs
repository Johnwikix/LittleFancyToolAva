using System;
using System.Collections.Generic;
using System.Text;

namespace LittleFancyToolAva.Models;

public sealed class RabbitMqMessage
{
    public ulong DeliveryTag { get; set; }
    public string Exchange { get; set; } = string.Empty;
    public string RoutingKey { get; set; } = string.Empty;
    public string QueueName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "text/plain";
    public IDictionary<string, object?>? Headers { get; set; }
    public byte[] Body { get; set; } = [];
    public bool Redelivered { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string DisplayExchange => string.IsNullOrEmpty(Exchange) ? "(default)" : Exchange;

    public string TimestampText => Timestamp.ToString("HH:mm:ss.fff");

    public string BodyPreview
    {
        get
        {
            if (Body is null || Body.Length == 0) return string.Empty;
            try
            {
                var text = Encoding.UTF8.GetString(Body);
                if (text.Length > 80) return text[..80] + "...";
                return text;
            }
            catch
            {
                return $"<{Body.Length}B>";
            }
        }
    }
}