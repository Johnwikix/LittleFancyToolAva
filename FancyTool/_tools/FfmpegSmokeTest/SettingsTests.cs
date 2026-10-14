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
            Check(app.Preferences.UseSuperResolutionNpu, "NPU acceleration should default on for legacy settings");
            Check(AppPaths.ModelsDirectory == AppPaths.DefaultModelsDirectory, "Default model root changed");
            Directory.CreateDirectory(AppPaths.ModelsDirectory);
            string oldModel = Path.Combine(AppPaths.ModelsDirectory, "existing-model.marker");
            File.WriteAllText(oldModel, "keep");
            vm.PickModelsDirectoryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(app.Preferences.ModelStorageDirectory == custom && vm.ModelsDirectory == custom, "Folder selection did not update settings");
            Check(files.LoadState()!.Preferences!.ModelStorageDirectory == custom, "Folder selection was not persisted immediately");
            Check(AppPaths.ModelsDirectory == AppPaths.DefaultModelsDirectory, "Active model directory changed before restart");
            Check(File.Exists(oldModel) && !File.Exists(Path.Combine(custom, "existing-model.marker")), "Existing models were moved");
            vm.OpenModelsDirectoryCommand.Execute(null);
            Check(dialogs.Opened == custom, "Open folder used the wrong directory");
            dialogs.Folder = null;
            vm.PickModelsDirectoryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(app.Preferences.ModelStorageDirectory == custom, "Canceled picker changed preferences");
            string blocked = Path.Combine(workDir, "not-a-folder");
            File.WriteAllText(blocked, "file");
            dialogs.Folder = Path.Combine(blocked, "child");
            vm.PickModelsDirectoryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.HasModelStorageError && files.LoadState()!.Preferences!.ModelStorageDirectory == custom, "Invalid directory was accepted");
            if (OperatingSystem.IsWindows())
            {
                dialogs.Folder = Path.Combine(workDir, "save-failure");
                using (var held = new FileStream(preferenceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    vm.PickModelsDirectoryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                    Check(vm.HasModelStorageError && app.Preferences.ModelStorageDirectory == custom, "Failed save did not roll back preferences");
                }
            }
            dialogs.Folder = custom;
            vm.PickModelsDirectoryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!vm.HasModelStorageError, "Successful retry did not clear the error");
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
            string marker = Path.Combine(custom, "4x-ClearRealityV1_fp16.onnx");
            File.WriteAllText(marker, "availability marker only");
            Check(video.IsModelAvailable(SuperResolutionModel.SpanClearRealityV1), "Video models ignored the custom root");
            // Bind a fresh settings VM to the preferences instance loaded on restart.
            vm = new SettingsViewModel(app, dialogs, files);
            vm.ResetModelsDirectoryCommand.Execute(null);
            Check(files.LoadState()!.Preferences!.ModelStorageDirectory == string.Empty, "Restore default was not persisted");
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
            Console.WriteLine("Settings OK: persistence, restart, defaults, legacy acceleration, cancellation, invalid path, save rollback, shared roots, bilingual bindings and light/dark layouts");
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
