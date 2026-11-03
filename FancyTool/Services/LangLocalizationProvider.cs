using Lang.Avalonia;

namespace FancyToolAva.Services;

internal sealed class LangLocalizationProvider : ILocalizationProvider
{
    public string? GetString(string key) => I18nManager.Instance.GetResource(key);
}
