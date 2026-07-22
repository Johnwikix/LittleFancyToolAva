namespace LittleFancyToolAva.Models.ViewStates;

public class RabbitMqViewState
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";

    public string ExchangeName { get; set; } = string.Empty;
    public string ExchangeTypeIndex { get; set; } = "0";
    public string RoutingKey { get; set; } = string.Empty;
    public string HeadersJson { get; set; } = string.Empty;
    public string PublishExchangeText { get; set; } = string.Empty;
    public bool ExchangeAutoDeclare { get; set; } = true;
    public bool ExchangeIsPersistent { get; set; }

    public string QueueName { get; set; } = string.Empty;
    public string PublishQueueText { get; set; } = string.Empty;
    public bool QueueAutoDeclare { get; set; } = true;
    public bool QueueIsPersistent { get; set; }

    public string ConsumeQueue { get; set; } = string.Empty;
    public int AckModeIndex { get; set; } = 1;
    public bool IsHexSend { get; set; }
    public bool IsHexDisplay { get; set; }
}