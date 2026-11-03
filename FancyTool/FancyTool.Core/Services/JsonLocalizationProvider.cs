using System.Globalization;
using System.Text.Json;

namespace FancyToolAva.Services;

public interface ILocalizationProvider
{
    string? GetString(string key);
}

/// <summary>Reads the same nested i18n JSON files used by the desktop UI.</summary>
public sealed class JsonLocalizationProvider : ILocalizationProvider
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public JsonLocalizationProvider(CultureInfo culture, string? directory = null)
    {
        Directory = directory ?? Path.Combine(AppContext.BaseDirectory, "i18n");
        string path = ResolveFile(culture);
        if (path != null) Load(path);
    }

    public string Directory { get; }

    public string? GetString(string key) => _values.TryGetValue(key, out var value) ? value : null;

    private string? ResolveFile(CultureInfo culture)
    {
        foreach (string name in new[] { culture.Name, culture.TwoLetterISOLanguageName, "en-US" })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string path = Path.Combine(Directory, name + ".json");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private void Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        Flatten(document.RootElement, null);
    }

    private void Flatten(JsonElement element, string? prefix)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            if (prefix == null && property.Name is "language" or "description" or "cultureName") continue;
            string key = prefix == null ? property.Name : prefix + "." + property.Name;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Flatten(property.Value, key);
                    break;
                case JsonValueKind.String:
                    _values[key] = property.Value.GetString() ?? key;
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    _values[key] = property.Value.ToString();
                    break;
            }
        }
    }
}
