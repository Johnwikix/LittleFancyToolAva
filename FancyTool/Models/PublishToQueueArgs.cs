namespace FancyToolAva.Models;

public sealed class PublishToQueueArgs
{
    public string QueueName { get; set; } = string.Empty;
    public bool AutoDeclare { get; set; } = true;
    public byte[] Body { get; set; } = [];
    public string ContentType { get; set; } = "text/plain";
    public bool Persistent { get; set; }
}