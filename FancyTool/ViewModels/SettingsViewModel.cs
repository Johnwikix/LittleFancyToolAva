using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lang.Avalonia;
using FancyToolAva.Models;
using FancyToolAva.Services;

namespace FancyToolAva.ViewModels
{
    public partial class SettingsViewModel : ViewModelBase
    {
        private readonly AppObserveModel _app;
        private readonly IFileDialogService _fileDialogService;

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
        public string LabelBackdrop => LocalizationStrings.Current["Settings.Label_Backdrop"];
        public string CaptionBackdrop => LocalizationStrings.Current["Settings.Caption_Backdrop"];
        public string LabelAcrylicTint => LocalizationStrings.Current["Settings.Label_AcrylicTint"];
        public string CaptionAcrylicTint => LocalizationStrings.Current["Settings.Caption_AcrylicTint"];
        public string LabelTintOpacity => LocalizationStrings.Current["Settings.Label_TintOpacity"];
        public string LabelMaterialOpacity => LocalizationStrings.Current["Settings.Label_MaterialOpacity"];
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

        // Mica / 亚克力等系统背景依赖 Windows DWM，其它平台上整个选项禁用
        public bool CanUseBackdrop => OperatingSystem.IsWindows();

        public bool CanCustomizeAcrylic =>
            CanUseBackdrop && App.Preferences.Backdrop == BackdropMode.CustomAcrylic;

        private IReadOnlyList<string> _backdropOptions = [];

        public IReadOnlyList<string> BackdropOptions => _backdropOptions;

        private IReadOnlyList<string> _tintOptions = [];

        public IReadOnlyList<string> TintOptions => _tintOptions;

        public string LabelConnectionTimeout => LocalizationStrings.Current["Settings.Label_ConnectionTimeout"];
        public string CaptionConnectionTimeout => LocalizationStrings.Current["Settings.Caption_ConnectionTimeout"];
        public string GroupAbout => LocalizationStrings.Current["Settings.Group_About"];
        public string AboutTitle => LocalizationStrings.Current["Settings.About_Title"];
        public string AboutDesc => LocalizationStrings.Current["Settings.About_Desc"];
        public string LabelOpenLogs => LocalizationStrings.Current["Settings.Label_OpenLogs"];
        public string CaptionOpenLogs => LocalizationStrings.Current["Settings.Caption_OpenLogs"];
        // LocalizationStrings.Current 只装 Settings.* 键集，Common 键走带兜底的 LocalizationRegistry
        public string ButtonOpenLogs => LocalizationRegistry.Get("Common.Button_Open");

        public LanguageOption? SelectedLanguage
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                    OnSelectedLanguageChanged(value);
            }
        }

        public SettingsViewModel(AppObserveModel app, IFileDialogService fileDialogService)
        {
            _app = app;
            _fileDialogService = fileDialogService;
            _app.Preferences.PropertyChanged += OnPreferencesPropertyChanged;
            var currentCulture = I18nManager.Instance.Culture?.Name ?? "en-US";
            SelectedLanguage = AvailableLanguages.FirstOrDefault(l => l.Culture == currentCulture)
                            ?? AvailableLanguages[0];
            RefreshBackdropOptions();
            RefreshSrTileOptions();
        }

        private void OnPreferencesPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppPreferences.UseSuperResolutionDml))
                OnPropertyChanged(nameof(CanSelectSrTileSize));
            if (e.PropertyName == nameof(AppPreferences.BackdropIndex))
                OnPropertyChanged(nameof(CanCustomizeAcrylic));
        }

        public void RefreshBackdropOptions()
        {
            _backdropOptions =
            [
                LocalizationStrings.Current["Settings.Backdrop_Mica"],
                LocalizationStrings.Current["Settings.Backdrop_Acrylic"],
                LocalizationStrings.Current["Settings.Backdrop_CustomAcrylic"],
                LocalizationStrings.Current["Settings.Backdrop_Transparent"]
            ];
            _tintOptions =
            [
                LocalizationStrings.Current["Settings.Tint_FollowTheme"],
                LocalizationStrings.Current["Settings.Tint_Blue"],
                LocalizationStrings.Current["Settings.Tint_Purple"],
                LocalizationStrings.Current["Settings.Tint_Green"],
                LocalizationStrings.Current["Settings.Tint_Orange"],
                LocalizationStrings.Current["Settings.Tint_Red"]
            ];
            OnPropertyChanged(nameof(BackdropOptions));
            OnPropertyChanged(nameof(TintOptions));
            OnPropertyChanged(nameof(LabelBackdrop));
            OnPropertyChanged(nameof(CaptionBackdrop));
            OnPropertyChanged(nameof(LabelAcrylicTint));
            OnPropertyChanged(nameof(CaptionAcrylicTint));
            OnPropertyChanged(nameof(LabelTintOpacity));
            OnPropertyChanged(nameof(LabelMaterialOpacity));
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

        [RelayCommand]
        private void OpenLogFolder()
        {
            // 与 Program.cs 里 Serilog 的落盘目录保持一致；不存在则先建，避免打开失败
            string dir = Path.Combine(AppPaths.DataDirectory, "logs");
            Directory.CreateDirectory(dir);
            _fileDialogService.OpenInExplorer(dir);
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
            RefreshBackdropOptions();
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
            OnPropertyChanged(nameof(LabelOpenLogs));
            OnPropertyChanged(nameof(CaptionOpenLogs));
            OnPropertyChanged(nameof(ButtonOpenLogs));
        }
    }
}
