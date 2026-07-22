namespace LittleFancyToolAva.Models;

public sealed class RabbitMqConnectionConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
}