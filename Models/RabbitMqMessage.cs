using System.Text;

namespace LittleFancyToolAva.Models;

public sealed class RabbitMqMessage
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private const int PreviewMaxChars = 80;

    private byte[] _body = [];
    private Lazy<string> _bodyPreview;

    public RabbitMqMessage()
    {
        _bodyPreview = new Lazy<string>(ComputeBodyPreview);
    }

    public ulong DeliveryTag { get; set; }
    public string Exchange { get; set; } = string.Empty;
    public string RoutingKey { get; set; } = string.Empty;
    public string QueueName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "text/plain";
    public IDictionary<string, object?>? Headers { get; set; }
    public bool Redelivered { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public byte[] Body
    {
        get => _body;
        set
        {
            _body = value ?? [];
            _bodyPreview = new Lazy<string>(ComputeBodyPreview);
        }
    }

    public string DisplayExchange => string.IsNullOrEmpty(Exchange) ? "(default)" : Exchange;

    public string TimestampText => Timestamp.ToString("HH:mm:ss.fff");

    public string BodyPreview => _bodyPreview.Value;

    private string ComputeBodyPreview()
    {
        if (_body.Length == 0) return string.Empty;
        try
        {
            int maxChars = Math.Min(PreviewMaxChars, _body.Length);
            string text = Utf8.GetString(_body, 0, _body.Length);
            if (text.Length > maxChars) return string.Concat(text.AsSpan(0, maxChars), "...");
            return text;
        }
        catch
        {
            return $"<{_body.Length}B>";
        }
    }
}