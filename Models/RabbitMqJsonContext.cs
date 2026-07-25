using System.Text.Json.Serialization;

namespace LittleFancyToolAva.Models;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(Dictionary<string, object?>))]
internal partial class RabbitMqJsonContext : JsonSerializerContext
{
}