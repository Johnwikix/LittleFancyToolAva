namespace LittleFancyToolAva.Models;

public enum ConnectionEventType
{
    Connected,
    Disconnected,
    ClientConnected,
    ClientDisconnected,
    Lost,
    Error,
    Reconnecting,
    PingTimeout,
    LineDisconnect
}

public class ConnectionEventArgs : EventArgs
{
    public ConnectionEventType Type { get; }
    public string Message { get; }
    public Exception? Exception { get; }
    public string? Endpoint { get; }
    public DateTime Timestamp { get; }

    public ConnectionEventArgs(ConnectionEventType type, string message, Exception? exception = null, string? endpoint = null)
    {
        Type = type;
        Message = message;
        Exception = exception;
        Endpoint = endpoint;
        Timestamp = DateTime.Now;
    }
}
