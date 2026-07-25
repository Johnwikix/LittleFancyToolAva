using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LittleFancyToolAva.Models
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
    }
}
