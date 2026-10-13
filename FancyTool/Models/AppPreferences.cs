using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using FancyToolAva.Services;

namespace FancyToolAva.Models
{
    public enum ThemeMode
    {
        System = 0,
        Light = 1,
        Dark = 2
    }

    public partial class AppPreferences : ObservableObject
    {
        public int ThemeIndex
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnThemeIndexChanged(value);
                }
            }
        } = (int)ThemeMode.System;

        private void OnThemeIndexChanged(int value)
        {
            if (Application.Current is { } app)
            {
                app.RequestedThemeVariant = value switch
                {
                    (int)ThemeMode.Light => ThemeVariant.Light,
                    (int)ThemeMode.Dark => ThemeVariant.Dark,
                    _ => ThemeVariant.Default
                };
            }
        }

        public ThemeVariant ToAvalonia() => ThemeIndex switch
        {
            (int)ThemeMode.Light => ThemeVariant.Light,
            (int)ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        public ThemeMode Theme
        {
            get => (ThemeMode)ThemeIndex;
            set => ThemeIndex = (int)value;
        }

        public int BackdropIndex
        {
            get;
            // ComboBox 被禁用/重置时会推送 -1（无选中），不应覆盖用户的选择
            set => SetProperty(ref field, value < 0 ? field : Math.Clamp(value, 0, 4));
        } = (int)BackdropMode.Mica;

        public BackdropMode Backdrop
        {
            get => (BackdropMode)BackdropIndex;
            set => BackdropIndex = (int)value;
        }

        /// <summary>自定义亚克力色调预设索引，0 = 跟随主题。</summary>
        public int AcrylicTintIndex
        {
            get;
            // -1 是 ComboBox 失焦/禁用时的“无选中”哨兵，忽略而非回退 0
            set => SetProperty(ref field, value < 0 ? field : Math.Clamp(value, 0, BackdropService.TintPresetCount - 1));
        } = 0;

        /// <summary>着色层不透明度（0-100）。</summary>
        public int AcrylicTintOpacity
        {
            get;
            set => SetProperty(ref field, Math.Clamp(value, 0, 100));
        } = 50;

        /// <summary>材质层不透明度（0-100）。</summary>
        public int AcrylicMaterialOpacity
        {
            get;
            set => SetProperty(ref field, Math.Clamp(value, 0, 100));
        } = 65;

        private int _connectionTimeoutSec = 5;

        public int ConnectionTimeoutSec
        {
            get => _connectionTimeoutSec;
            set => SetProperty(ref _connectionTimeoutSec, Math.Clamp(value, 1, 30));
        }

        private string _language = "en-US";

        public string Language
        {
            get => _language;
            set => SetProperty(ref _language, string.IsNullOrWhiteSpace(value) ? "en-US" : value);
        }

        private bool _useSuperResolutionDml = true;

        public bool UseSuperResolutionDml
        {
            get => _useSuperResolutionDml;
            set => SetProperty(ref _useSuperResolutionDml, value);
        }

        /// <summary>Gets or sets whether image enhancement prefers Intel NPU inference before GPU inference.</summary>
        public bool UseSuperResolutionNpu { get => field; set => SetProperty(ref field, value); }

        public int SuperResolutionTileSizeIndex
        {
            get => field;
            set => SetProperty(ref field, Math.Clamp(value, 0, 3));
        } = 2;
    }
}
