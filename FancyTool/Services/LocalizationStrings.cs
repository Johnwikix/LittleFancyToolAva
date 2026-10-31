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
            "License.Title",
            "License.CheckingTitle",
            "License.PackageMismatchTitle",
            "License.VerificationTitle",
            "License.Checking",
            "License.Required",
            "License.VerificationFailed",
            "License.PackageMismatch",
            "License.FullLicense",
            "License.Unrestricted",
            "License.TrialActive",
            "License.TrialUntil",
            "Settings.Page_Title",
            "Settings.Page_Subtitle",
            "Settings.Group_Appearance",
            "Settings.Label_Theme",
            "Settings.Caption_Theme",
            "Settings.Theme_System",
            "Settings.Theme_Light",
            "Settings.Theme_Dark",
            "Settings.Label_Backdrop",
            "Settings.Caption_Backdrop",
            "Settings.Backdrop_Mica",
            "Settings.Backdrop_Acrylic",
            "Settings.Backdrop_CustomAcrylic",
            "Settings.Backdrop_Transparent",
            "Settings.Label_AcrylicTint",
            "Settings.Caption_AcrylicTint",
            "Settings.Tint_FollowTheme",
            "Settings.Tint_Blue",
            "Settings.Tint_Purple",
            "Settings.Tint_Green",
            "Settings.Tint_Orange",
            "Settings.Tint_Red",
            "Settings.Label_TintOpacity",
            "Settings.Label_MaterialOpacity",
            "Settings.Group_Language",
            "Settings.Label_Language",
            "Settings.Caption_Language",
            "Settings.Group_Network",
            "Settings.Label_ConnectionTimeout",
            "Settings.Caption_ConnectionTimeout",
            "Settings.Group_Image",
            "Settings.Group_Models",
            "Settings.Label_ModelsDirectory",
            "Settings.Caption_ModelsDirectory",
            "Settings.Button_SelectModelsDirectory",
            "Settings.Button_OpenModelsDirectory",
            "Settings.Button_ResetModelsDirectory",
            "Settings.Msg_ModelsDirectoryError",
            "Settings.Label_SrTileSize",
            "Settings.Caption_SrTileSize",
            "Settings.SrTile_MinMemory",
            "Settings.SrTile_Memory",
            "Settings.SrTile_Balanced",
            "Settings.SrTile_Quality",
            "Settings.Group_About",
            "Settings.About_Title",
            "Settings.About_Desc",
            "Settings.Label_OpenLogs",
            "Settings.Caption_OpenLogs",
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
