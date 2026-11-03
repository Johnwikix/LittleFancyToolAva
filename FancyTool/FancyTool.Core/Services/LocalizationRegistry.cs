using System.Globalization;

namespace FancyToolAva.Services
{
    public sealed record LanguageOption(string Culture, string NativeName, string EnglishName);

    /// <summary>Code-side lookup used by Core services; the host installs a language provider.</summary>
    public static class LocalizationRegistry
    {
        public static ILocalizationProvider? Provider { get; set; }

        public static System.Collections.Generic.List<LanguageOption> Available { get; } =
        [
            new LanguageOption("en-US", "English", "English"),
            new LanguageOption("zh-CN", "简体中文", "Simplified Chinese"),
        ];

        public static string Get(string key)
        {
            try
            {
                return Provider?.GetString(key) ?? key;
            }
            catch
            {
                return key;
            }
        }

        public static string Get(string key, params object[] args)
        {
            var template = Get(key);
            if (args is null || args.Length == 0) return template;
            try
            {
                return string.Format(CultureInfo.CurrentCulture, template, args);
            }
            catch
            {
                return template;
            }
        }

        public static CultureInfo ResolveInitialCulture(string? storedLanguage)
        {
            if (!string.IsNullOrWhiteSpace(storedLanguage))
            {
                try
                {
                    return new CultureInfo(storedLanguage);
                }
                catch
                {
                }
            }

            var ui = CultureInfo.CurrentUICulture.Name;
            return ui.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase)
                ? new CultureInfo("zh-CN")
                : new CultureInfo("en-US");
        }
    }
}
