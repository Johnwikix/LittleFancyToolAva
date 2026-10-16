using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FancyToolAva;
using FancyToolAva.Models;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.Services;
using FancyToolAva.Services.Lyrics;
using FancyToolAva.ViewModels;
using FancyToolAva.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunSettingsCases(string workDir, bool inspect)
    {
        try
        {
            string data = Path.GetFullPath(Path.Combine(workDir, "settings-data"));
            string custom = Path.GetFullPath(Path.Combine(workDir, "自定义模型 storage"));
            Environment.SetEnvironmentVariable("FANCYTOOL_DATA_DIRECTORY", data);
            Environment.SetEnvironmentVariable("FANCYTOOL_LYRICS_MODELS", null);
            Directory.CreateDirectory(data);
            string preferenceFile = Path.Combine(data, "preferences.json");
            File.WriteAllText(preferenceFile, "{\"Preferences\":{\"Language\":\"zh-CN\",\"UseSuperResolutionDml\":false}}");
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            var files = new FileService(NullLogger<FileService>.Instance);
            var app = new AppObserveModel();
            var views = new SettingsTestViewStates();
            var host = new ApplicationHostService(files, app, views, NullLogger<ApplicationHostService>.Instance);
            host.LoadState();
            var dialogs = new SettingsTestDialogs { Folder = custom };
            var vm = new SettingsViewModel(app, dialogs, files);
            void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
            void Complete(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
            Check(app.Preferences.UseSuperResolutionNpu, "NPU acceleration should default on for legacy settings");
            Check(AppPaths.ModelsDirectory == AppPaths.DefaultModelsDirectory, "Default model root changed");
            Directory.CreateDirectory(AppPaths.ModelsDirectory);
            string oldModel = Path.Combine(AppPaths.ModelsDirectory, "existing-model.marker");
            File.WriteAllText(oldModel, "keep");
            Directory.CreateDirectory(custom);
            File.WriteAllText(Path.Combine(custom, "existing-model.marker"), "keep");
            string partial = oldModel + ".partial";
            File.WriteAllText(partial, "unfinished download");
            Directory.CreateDirectory(Path.Combine(AppPaths.ModelsDirectory, "Qwen3Lyrics", "asr"));
            File.WriteAllText(Path.Combine(AppPaths.ModelsDirectory, "Qwen3Lyrics", "asr", "model.marker"), "nested");
            Complete(vm.PickModelsDirectoryCommand.ExecuteAsync(null));
            Check(app.Preferences.ModelStorageDirectory == custom && vm.ModelsDirectory == custom, "Folder selection did not update settings");
            Check(files.LoadState()!.Preferences!.ModelStorageDirectory == custom, "Folder selection was not persisted immediately");
            Check(AppPaths.ModelsDirectory == custom, "Active model directory did not change immediately");
            Check(!File.Exists(oldModel) && File.ReadAllText(Path.Combine(custom, "existing-model.marker")) == "keep", "Existing models were not migrated");
            Check(File.Exists(partial) && !File.Exists(Path.Combine(custom, "existing-model.marker.partial")), "An unfinished model download was migrated");
            Check(File.ReadAllText(Path.Combine(custom, "Qwen3Lyrics", "asr", "model.marker")) == "nested", "Nested model files were not migrated");
            vm.OpenModelsDirectoryCommand.Execute(null);
            Check(dialogs.Opened == custom, "Open folder used the wrong directory");
            dialogs.Folder = null;
            Complete(vm.PickModelsDirectoryCommand.ExecuteAsync(null));
            Check(app.Preferences.ModelStorageDirectory == custom, "Canceled picker changed preferences");
            string blocked = Path.Combine(workDir, "not-a-folder");
            File.WriteAllText(blocked, "file");
            dialogs.Folder = Path.Combine(blocked, "child");
            Complete(vm.PickModelsDirectoryCommand.ExecuteAsync(null));
            Check(vm.HasModelStorageError && files.LoadState()!.Preferences!.ModelStorageDirectory == custom, "Invalid directory was accepted");
            if (OperatingSystem.IsWindows())
            {
                dialogs.Folder = Path.Combine(workDir, "save-failure");
                using (var held = new FileStream(preferenceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Complete(vm.PickModelsDirectoryCommand.ExecuteAsync(null));
                    Check(vm.HasModelStorageError && app.Preferences.ModelStorageDirectory == custom, "Failed save did not roll back preferences");
                    Check(AppPaths.ModelsDirectory == custom && File.Exists(Path.Combine(custom, "existing-model.marker")), "Failed save removed the active models");
                }
            }
            dialogs.Folder = custom;
            Complete(vm.PickModelsDirectoryCommand.ExecuteAsync(null));
            Check(!vm.HasModelStorageError, "Successful retry did not clear the error");
            string conflict = Path.GetFullPath(Path.Combine(workDir, "conflicting-models"));
            Directory.CreateDirectory(conflict);
            File.WriteAllText(Path.Combine(conflict, "existing-model.marker"), "different");
            dialogs.Folder = conflict;
            Complete(vm.PickModelsDirectoryCommand.ExecuteAsync(null));
            Check(vm.HasModelStorageError && AppPaths.ModelsDirectory == custom
                && File.ReadAllText(Path.Combine(conflict, "existing-model.marker")) == "different", "Migration overwrote a conflicting model or changed the active root");
            dialogs.Folder = Path.Combine(custom, "nested-root");
            Complete(vm.PickModelsDirectoryCommand.ExecuteAsync(null));
            Check(vm.HasModelStorageError && AppPaths.ModelsDirectory == custom, "Overlapping model folders were accepted");
            // Migration waits for model installation/session creation to finish.
            string synchronized = Path.GetFullPath(Path.Combine(workDir, "synchronized-models"));
            dialogs.Folder = synchronized;
            using (ModelStorage.Acquire(CancellationToken.None))
            {
                var pending = vm.PickModelsDirectoryCommand.ExecuteAsync(null);
                Check(vm.IsMovingModels && !pending.IsCompleted && !vm.ResetModelsDirectoryCommand.CanExecute(null), "Migration did not wait for active model access");
            }
            PumpUntil(() => !vm.IsMovingModels);
            Check(AppPaths.ModelsDirectory == synchronized, "Waiting migration did not complete");
            dialogs.Folder = custom;
            Complete(vm.PickModelsDirectoryCommand.ExecuteAsync(null));
            host.LoadState(); // Simulate the next launch before constructing model consumers.
            Check(AppPaths.ModelsDirectory == custom, "Saved custom directory was not applied at startup");
            Check(LyricsModelService.DefaultDirectory == Path.Combine(custom, "Qwen3Lyrics"), "Default lyrics models ignored model management");
            string lyricsData = Path.GetFullPath(Path.Combine(workDir, "lyrics-queue"));
            Environment.SetEnvironmentVariable("FANCYTOOL_LYRICS_DATA", lyricsData);
            Directory.CreateDirectory(lyricsData);
            string queuePath = Path.Combine(lyricsData, "queue.json");
            var lyricsModels = ((App)Application.Current!).TryGetService<LyricsModelService>()!;
            var extraction = ((App)Application.Current!).TryGetService<LyricsExtractionService>()!;
            foreach (string stored in new[] { Path.Combine(AppPaths.DefaultModelsDirectory, "Qwen3Lyrics"), Path.Combine(workDir, "explicit-lyrics-models") })
            {
                File.WriteAllText(queuePath, JsonSerializer.Serialize(new LyricsQueueState { Options = new LyricsOptions { ModelDirectory = stored } }));
                using var lyrics = new LyricsExtractionViewModel(extraction, lyricsModels, dialogs, NullLogger<LyricsExtractionViewModel>.Instance);
                string expected = stored.Contains("explicit-lyrics-models", StringComparison.Ordinal) ? stored : Path.Combine(custom, "Qwen3Lyrics");
                Check(lyrics.ModelsDirectory == expected, "Legacy default migration overwrote an explicit lyrics folder or ignored model management");
            }
            using var http = new HttpClient();
            var downloader = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
            var video = new VideoSuperResolutionService(downloader, NullLogger<VideoSuperResolutionService>.Instance);
            string marker = Path.Combine(custom, "VideoSr", VideoSrModelCatalog.Get(VideoSrModel.Anime4KCnnS).FullPrecision.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "availability marker only");
            Check(video.IsModelAvailable(VideoSrModel.Anime4KCnnS), "Video models ignored the custom root");
            var interpolation = new VideoInterpolationService(downloader, NullLogger<VideoInterpolationService>.Instance);
            string interpolationMarker = Path.Combine(custom, "Interpolation", VideoInterpolationService.GetModel(VideoInterpolationModel.Rife425Lite).FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(interpolationMarker)!);
            File.WriteAllText(interpolationMarker, "availability marker only");
            Check(interpolation.IsModelAvailable(VideoInterpolationModel.Rife425Lite), "Interpolation ignored the custom root");
            // Bind a fresh settings VM to the preferences instance loaded on restart.
            vm = new SettingsViewModel(app, dialogs, files);
            Complete(vm.ResetModelsDirectoryCommand.ExecuteAsync(null));
            Check(files.LoadState()!.Preferences!.ModelStorageDirectory == string.Empty, "Restore default was not persisted");
            Check(!File.Exists(marker) && video.IsModelAvailable(VideoSrModel.Anime4KCnnS), "Reset did not migrate video models or update the existing service");
            Check(!File.Exists(interpolationMarker) && interpolation.IsModelAvailable(VideoInterpolationModel.Rife425Lite), "Reset did not migrate interpolation models or update the existing service");
            host.LoadState();
            Check(AppPaths.ModelsDirectory == AppPaths.DefaultModelsDirectory, "Default root did not return after restart");
            Check(!Directory.EnumerateFiles(custom, ".fancytool-write-*").Any(), "Write probes were left behind");
            vm = new SettingsViewModel(app, dialogs, files);
            var view = new SettingsView { DataContext = vm };
            var window = new Window { Title = "Settings model management smoke test", Width = 1000, Height = 900, Content = view };
            window.Show();
            PumpUntil(() => view.Bounds.Width > 0);
            var pathControl = view.GetVisualDescendants().OfType<TextBox>().Single(c => AutomationProperties.GetAutomationId(c) == "SettingsModelsDirectory");
            var tile = view.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c) == "SettingsSrTileSize");
            Check(pathControl.IsReadOnly && pathControl.Text == AppPaths.DefaultModelsDirectory && tile.IsEnabled, "Settings bindings or tile control are incorrect");
            Check(!view.GetVisualDescendants().OfType<CheckBox>().Any(), "Removed GPU switch is still present");
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current!.RequestedThemeVariant = theme;
                window.RequestedThemeVariant = theme;
                window.Width = theme == ThemeVariant.Light ? 1000 : 650;
                pathControl.BringIntoView();
                PumpUntil(() => window.ActualThemeVariant == theme && Math.Abs(view.Bounds.Width - window.ClientSize.Width) < 2);
                view.Background = window.Background;
                pathControl.BringIntoView();
                PumpUntil(() => true);
                window.UpdateLayout();
                using var image = new RenderTargetBitmap(new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height), new Vector(96, 96));
                image.Render(view);
                image.Save(Path.Combine(workDir, theme == ThemeVariant.Light ? "settings-light.png" : "settings-dark-narrow.png"));
            }
            vm.SelectedLanguage = vm.AvailableLanguages.Single(l => l.Culture == "en-US");
            Check(vm.GroupModels == "Model Management", "Model settings did not localize");
            vm.SelectedLanguage = vm.AvailableLanguages.Single(l => l.Culture == "zh-CN");
            Check(tile.SelectedIndex == app.Preferences.SuperResolutionTileSizeIndex, "Language change cleared the memory tier selection");
            if (inspect)
            {
                window.RequestedThemeVariant = ThemeVariant.Light;
                Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                window.Width = 1000;
                view.Background = window.Background;
                pathControl.BringIntoView();
                using var done = new CancellationTokenSource();
                window.Closed += (_, _) => done.Cancel();
                Console.WriteLine($"UI ready: PID={Environment.ProcessId}; custom folder={custom}");
                Dispatcher.UIThread.MainLoop(done.Token);
            }
            else window.Close();
            Console.WriteLine("Settings OK: verified migration, immediate shared roots, nested files, conflicts, access synchronization, save rollback, reset, restart, cancellation, bilingual bindings and light/dark layouts");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("Settings FAIL: " + ex); return 1; }
    }

    private sealed class SettingsTestViewStates : IViewStateService
    {
        public void Register(IViewState view) { }
        public void Unregister(IViewState view) { }
        public void SaveAll() { }
        public void LoadAll() { }
    }

    private sealed class SettingsTestDialogs : IFileDialogService
    {
        public string? Folder { get; set; }
        public string? Opened { get; private set; }
        public Task<string?> PickFolderAsync(string title) => Task.FromResult(Folder);
        public void OpenInExplorer(string path) => Opened = path;
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FilePickerFileType>? filters = null) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>?> PickOpenFilesAsync(string title, IReadOnlyList<FilePickerFileType>? filters = null) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task<string?> PickSaveFileAsync(string title, string? defaultFileName = null, IReadOnlyList<FilePickerFileType>? filters = null) => Task.FromResult<string?>(null);
    }
}
