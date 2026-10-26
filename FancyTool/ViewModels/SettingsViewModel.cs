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
        private readonly FileService _fileService;

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
        public string GroupModels => LocalizationStrings.Current["Settings.Group_Models"];
        public string LabelModelsDirectory => LocalizationStrings.Current["Settings.Label_ModelsDirectory"];
        public string CaptionModelsDirectory => LocalizationStrings.Current["Settings.Caption_ModelsDirectory"];
        public string ButtonSelectModelsDirectory => LocalizationStrings.Current["Settings.Button_SelectModelsDirectory"];
        public string ButtonOpenModelsDirectory => LocalizationStrings.Current["Settings.Button_OpenModelsDirectory"];
        public string ButtonResetModelsDirectory => LocalizationStrings.Current["Settings.Button_ResetModelsDirectory"];
        public string ModelsDirectory => AppPaths.ResolveModelsDirectory(App.Preferences.ModelStorageDirectory);
        /// <summary>Gets whether model files are being verified and moved to a new folder.</summary>
        public bool IsMovingModels
        {
            get;
            private set
            {
                if (!SetProperty(ref field, value)) return;
                PickModelsDirectoryCommand.NotifyCanExecuteChanged();
                ResetModelsDirectoryCommand.NotifyCanExecuteChanged();
            }
        }
        public bool HasModelStorageError => !string.IsNullOrEmpty(ModelStorageError);
        public string ModelStorageError
        {
            get;
            private set
            {
                if (SetProperty(ref field, value)) OnPropertyChanged(nameof(HasModelStorageError));
            }
        } = string.Empty;
        public string LabelSrTileSize => LocalizationStrings.Current["Settings.Label_SrTileSize"];
        public string CaptionSrTileSize => LocalizationStrings.Current["Settings.Caption_SrTileSize"];
        public string SrTileMinMemory => LocalizationStrings.Current["Settings.SrTile_MinMemory"];
        public string SrTileMemory => LocalizationStrings.Current["Settings.SrTile_Memory"];
        public string SrTileBalanced => LocalizationStrings.Current["Settings.SrTile_Balanced"];
        public string SrTileQuality => LocalizationStrings.Current["Settings.SrTile_Quality"];

        private readonly LocalizedOption[] _srTileOptions = [new("Settings.SrTile_MinMemory"),
            new("Settings.SrTile_Memory"), new("Settings.SrTile_Balanced"), new("Settings.SrTile_Quality")];

        public IReadOnlyList<LocalizedOption> SrTileOptions => _srTileOptions;
        public int SelectedSrTileSizeIndex
        {
            get => App.Preferences.SuperResolutionTileSizeIndex;
            set { if (value >= 0) App.Preferences.SuperResolutionTileSizeIndex = value; }
        }

        // Mica / 亚克力等系统背景依赖 Windows DWM，其它平台上整个选项禁用
        public bool CanUseBackdrop => OperatingSystem.IsWindows();

        public bool CanCustomizeAcrylic =>
            CanUseBackdrop && App.Preferences.Backdrop == BackdropMode.CustomAcrylic;

        private IReadOnlyList<string> _backdropOptions = [];
        private BackdropMode[] _backdropModes = [];

        public IReadOnlyList<string> BackdropOptions => _backdropOptions;

        public int BackdropOptionIndex
        {
            get
            {
                int index = Array.IndexOf(_backdropModes, App.Preferences.Backdrop);
                return index >= 0 ? index : 0;
            }
            set
            {
                if (value >= 0 && value < _backdropModes.Length)
                    App.Preferences.Backdrop = _backdropModes[value];
            }
        }

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

        public SettingsViewModel(AppObserveModel app, IFileDialogService fileDialogService, FileService fileService)
        {
            _app = app;
            _fileDialogService = fileDialogService;
            _fileService = fileService;
            _app.Preferences.PropertyChanged += OnPreferencesPropertyChanged;
            var currentCulture = I18nManager.Instance.Culture?.Name ?? "en-US";
            SelectedLanguage = AvailableLanguages.FirstOrDefault(l => l.Culture == currentCulture)
                            ?? AvailableLanguages[0];
            RefreshBackdropOptions();
            RefreshSrTileOptions();
        }

        private void OnPreferencesPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppPreferences.ModelStorageDirectory))
            {
                OnPropertyChanged(nameof(ModelsDirectory));
                ResetModelsDirectoryCommand.NotifyCanExecuteChanged();
            }
            if (e.PropertyName == nameof(AppPreferences.BackdropIndex))
            {
                OnPropertyChanged(nameof(BackdropOptionIndex));
                OnPropertyChanged(nameof(CanCustomizeAcrylic));
            }
            if (e.PropertyName == nameof(AppPreferences.SuperResolutionTileSizeIndex))
                OnPropertyChanged(nameof(SelectedSrTileSizeIndex));
        }

        public void RefreshBackdropOptions()
        {
            _backdropModes = BackdropService.SupportsMica
                ? [BackdropMode.Mica, BackdropMode.Acrylic, BackdropMode.CustomAcrylic, BackdropMode.Transparent]
                : [BackdropMode.Acrylic, BackdropMode.CustomAcrylic, BackdropMode.Transparent];
            _backdropOptions = _backdropModes.Select(mode => LocalizationStrings.Current[$"Settings.Backdrop_{mode}"]).ToArray();
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
            OnPropertyChanged(nameof(BackdropOptionIndex));
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
            foreach (var option in _srTileOptions) option.Refresh();
            OnPropertyChanged(nameof(SelectedSrTileSizeIndex));
            OnPropertyChanged(nameof(SrTileMinMemory));
            OnPropertyChanged(nameof(SrTileMemory));
            OnPropertyChanged(nameof(SrTileBalanced));
            OnPropertyChanged(nameof(SrTileQuality));
        }

        private bool CanPickModelsDirectory() => !IsMovingModels;

        [RelayCommand(CanExecute = nameof(CanPickModelsDirectory))]
        private async Task PickModelsDirectory()
        {
            try
            {
                string? folder = await _fileDialogService.PickFolderAsync(LabelModelsDirectory);
                if (folder != null) await SaveModelsDirectory(folder);
            }
            catch (Exception ex) { ShowModelStorageError(ex); }
        }

        private bool CanResetModelsDirectory() => !IsMovingModels && !string.IsNullOrEmpty(App.Preferences.ModelStorageDirectory);

        [RelayCommand(CanExecute = nameof(CanResetModelsDirectory))]
        private async Task ResetModelsDirectory()
        {
            try { await SaveModelsDirectory(string.Empty); }
            catch (Exception ex) { ShowModelStorageError(ex); }
        }

        private async Task SaveModelsDirectory(string directory)
        {
            IsMovingModels = true;
            ModelStorageError = string.Empty;
            try
            {
                using var access = await ModelStorage.AcquireAsync();
                string path = AppPaths.ResolveModelsDirectory(directory);
                string source = AppPaths.ModelsDirectory;
                var copied = await Task.Run(() => ModelStorage.PrepareMigration(source, path));
                string previous = App.Preferences.ModelStorageDirectory;
                App.Preferences.ModelStorageDirectory = string.IsNullOrWhiteSpace(directory) ? string.Empty : path;
                try { _fileService.SaveState(_app); }
                catch
                {
                    App.Preferences.ModelStorageDirectory = previous;
                    throw;
                }
                AppPaths.InitializeModelsDirectory(path);
                await Task.Run(() => ModelStorage.RemoveOriginals(copied));
            }
            finally { IsMovingModels = false; }
        }

        [RelayCommand]
        private void OpenModelsDirectory()
        {
            try
            {
                Directory.CreateDirectory(ModelsDirectory);
                _fileDialogService.OpenInExplorer(ModelsDirectory);
                ModelStorageError = string.Empty;
            }
            catch (Exception ex) { ShowModelStorageError(ex); }
        }

        private void ShowModelStorageError(Exception ex) => ModelStorageError =
            LocalizationStrings.Current["Settings.Msg_ModelsDirectoryError"] + " " + ex.Message;

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
            OnPropertyChanged(nameof(GroupModels));
            OnPropertyChanged(nameof(LabelModelsDirectory));
            OnPropertyChanged(nameof(CaptionModelsDirectory));
            OnPropertyChanged(nameof(ButtonSelectModelsDirectory));
            OnPropertyChanged(nameof(ButtonOpenModelsDirectory));
            OnPropertyChanged(nameof(ButtonResetModelsDirectory));
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
