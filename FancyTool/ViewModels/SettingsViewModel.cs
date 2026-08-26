using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lang.Avalonia;
using FancyToolAva.Models;
using FancyToolAva.Services;

namespace FancyToolAva.ViewModels
{
    public partial class SettingsViewModel : ViewModelBase
    {
        private readonly AppObserveModel _app;

        public AppObserveModel App => _app;

        public ObservableCollection<LanguageOption> AvailableLanguages { get; } =
            new(LocalizationRegistry.Available);

        public string PageTitle => LocalizationStrings.Current["Settings.Page_Title"];
        public string PageSubtitle => LocalizationStrings.Current["Settings.Page_Subtitle"];
        public string GroupAppearance => LocalizationStrings.Current["Settings.Group_Appearance"];
        public string LabelTheme => LocalizationStrings.Current["Settings.Label_Theme"];
        public string CaptionTheme => LocalizationStrings.Current["Settings.Caption_Theme"];
        public string ThemeSystem => LocalizationStrings.Current["Settings.Theme_System"];
        public string ThemeLight => LocalizationStrings.Current["Settings.Theme_Light"];
        public string ThemeDark => LocalizationStrings.Current["Settings.Theme_Dark"];
        public string GroupLanguage => LocalizationStrings.Current["Settings.Group_Language"];
        public string LabelLanguage => LocalizationStrings.Current["Settings.Label_Language"];
        public string CaptionLanguage => LocalizationStrings.Current["Settings.Caption_Language"];
        public string GroupNetwork => LocalizationStrings.Current["Settings.Group_Network"];
        public string GroupImage => LocalizationStrings.Current["Settings.Group_Image"];
        public string LabelSrGpu => LocalizationStrings.Current["Settings.Label_SrGpu"];
        public string CaptionSrGpu => LocalizationStrings.Current["Settings.Caption_SrGpu"];
        public string LabelSrTileSize => LocalizationStrings.Current["Settings.Label_SrTileSize"];
        public string CaptionSrTileSize => LocalizationStrings.Current["Settings.Caption_SrTileSize"];
        public string SrTileMinMemory => LocalizationStrings.Current["Settings.SrTile_MinMemory"];
        public string SrTileMemory => LocalizationStrings.Current["Settings.SrTile_Memory"];
        public string SrTileBalanced => LocalizationStrings.Current["Settings.SrTile_Balanced"];
        public string SrTileQuality => LocalizationStrings.Current["Settings.SrTile_Quality"];

        private IReadOnlyList<string> _srTileOptions = [];

        public IReadOnlyList<string> SrTileOptions => _srTileOptions;

        // DirectML is Windows-only; on other platforms the GPU controls are
        // disabled rather than clearing the user's toggle value.
        public bool CanUseSuperResolutionGpu => OperatingSystem.IsWindows();

        public bool CanSelectSrTileSize =>
            CanUseSuperResolutionGpu && App.Preferences.UseSuperResolutionDml;

        public string LabelConnectionTimeout => LocalizationStrings.Current["Settings.Label_ConnectionTimeout"];
        public string CaptionConnectionTimeout => LocalizationStrings.Current["Settings.Caption_ConnectionTimeout"];
        public string GroupAbout => LocalizationStrings.Current["Settings.Group_About"];
        public string AboutTitle => LocalizationStrings.Current["Settings.About_Title"];
        public string AboutDesc => LocalizationStrings.Current["Settings.About_Desc"];

        public LanguageOption? SelectedLanguage
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                    OnSelectedLanguageChanged(value);
            }
        }

        public SettingsViewModel(AppObserveModel app)
        {
            _app = app;
            _app.Preferences.PropertyChanged += OnPreferencesPropertyChanged;
            var currentCulture = I18nManager.Instance.Culture?.Name ?? "en-US";
            SelectedLanguage = AvailableLanguages.FirstOrDefault(l => l.Culture == currentCulture)
                            ?? AvailableLanguages[0];
            RefreshSrTileOptions();
        }

        private void OnPreferencesPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppPreferences.UseSuperResolutionDml))
                OnPropertyChanged(nameof(CanSelectSrTileSize));
        }

        public void RefreshSrTileOptions()
        {
            _srTileOptions = [SrTileMinMemory, SrTileMemory, SrTileBalanced, SrTileQuality];
            OnPropertyChanged(nameof(SrTileOptions));
            OnPropertyChanged(nameof(SrTileMinMemory));
            OnPropertyChanged(nameof(SrTileMemory));
            OnPropertyChanged(nameof(SrTileBalanced));
            OnPropertyChanged(nameof(SrTileQuality));
        }

        private void OnSelectedLanguageChanged(LanguageOption? value)
        {
            if (value is null) return;
            try
            {
                var culture = new CultureInfo(value.Culture);
                I18nManager.Instance.Culture = culture;
                _app.Preferences.Language = value.Culture;
                LocalizationStrings.Reload();
                NotifyAllTextChanged();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to switch language to {Culture}", value.Culture);
            }
        }

        private void NotifyAllTextChanged()
        {
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
            OnPropertyChanged(nameof(GroupAppearance));
            OnPropertyChanged(nameof(LabelTheme));
            OnPropertyChanged(nameof(CaptionTheme));
            OnPropertyChanged(nameof(ThemeSystem));
            OnPropertyChanged(nameof(ThemeLight));
            OnPropertyChanged(nameof(ThemeDark));
            OnPropertyChanged(nameof(GroupLanguage));
            OnPropertyChanged(nameof(LabelLanguage));
            OnPropertyChanged(nameof(CaptionLanguage));
            OnPropertyChanged(nameof(GroupNetwork));
            OnPropertyChanged(nameof(LabelConnectionTimeout));
            OnPropertyChanged(nameof(CaptionConnectionTimeout));
            OnPropertyChanged(nameof(GroupImage));
            OnPropertyChanged(nameof(LabelSrGpu));
            OnPropertyChanged(nameof(CaptionSrGpu));
            OnPropertyChanged(nameof(LabelSrTileSize));
            OnPropertyChanged(nameof(CaptionSrTileSize));
            RefreshSrTileOptions();
            OnPropertyChanged(nameof(GroupAbout));
            OnPropertyChanged(nameof(AboutTitle));
            OnPropertyChanged(nameof(AboutDesc));
        }
    }
}
