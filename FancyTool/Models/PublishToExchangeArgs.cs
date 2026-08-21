using System.Collections.Generic;

namespace FancyToolAva.Models;

public sealed class PublishToExchangeArgs
{
    public string Exchange { get; set; } = string.Empty;
    public string ExchangeType { get; set; } = "direct";
    public string RoutingKey { get; set; } = string.Empty;
    public bool AutoDeclare { get; set; } = true;
    public byte[] Body { get; set; } = [];
    public string ContentType { get; set; } = "text/plain";
    public bool Persistent { get; set; }
    public IDictionary<string, object?>? Headers { get; set; }
}