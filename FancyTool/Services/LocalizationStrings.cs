using System.Collections.Generic;
using System.Globalization;
using Lang.Avalonia;

namespace FancyToolAva.Services
{
    public static class LocalizationStrings
    {
        private static Dictionary<string, string> _current = [];
        private static readonly string[] _keys =
        [
            "Settings.Page_Title",
            "Settings.Page_Subtitle",
            "Settings.Group_Appearance",
            "Settings.Label_Theme",
            "Settings.Caption_Theme",
            "Settings.Theme_System",
            "Settings.Theme_Light",
            "Settings.Theme_Dark",
            "Settings.Group_Language",
            "Settings.Label_Language",
            "Settings.Caption_Language",
            "Settings.Group_Network",
            "Settings.Label_ConnectionTimeout",
            "Settings.Caption_ConnectionTimeout",
            "Settings.Group_About",
            "Settings.About_Title",
            "Settings.About_Desc",
        ];

        public static IReadOnlyDictionary<string, string> Current => _current;

        public static void Initialize(CultureInfo culture)
        {
            var dict = new Dictionary<string, string>(_keys.Length);
            foreach (var key in _keys)
            {
                dict[key] = I18nManager.Instance.GetResource(key) ?? key;
            }
            _current = dict;
        }

        public static void Reload()
        {
            var culture = I18nManager.Instance.Culture ?? new CultureInfo("en-US");
            Initialize(culture);
        }
    }
}
