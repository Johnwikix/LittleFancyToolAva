using System.Diagnostics;
using System.Text.Json;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FancyToolAva.Services.Lyrics;
using FancyToolAva.Utils;
using Lang.Avalonia;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.ViewModels;

public partial class LyricsExtractionViewModel : ViewModelBase, IDisposable
{
    private readonly LyricsExtractionService _extraction;
    private readonly LyricsModelService _models;
    private readonly IFileDialogService _dialogs;
    private readonly ILogger<LyricsExtractionViewModel> _logger;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _statePath;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _resume;
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".ogg", ".oga", ".m4a", ".m4b", ".aac", ".opus", ".wma", ".aiff", ".aif", ".ape", ".dsf", ".dff", ".wv", ".mpc", ".tta", ".tak", ".amr", ".mka", ".caf", ".ac3", ".dts", ".mp2", ".mp4", ".mkv", ".mov", ".webm" };
    public RangeObservableCollection<LyricsTaskItem> Tasks { get; } = [];
    public LyricsOptions Options { get; private set; } = new();
    public string[] LanguageLabels => LyricsExtractionService.Languages.Select(x => x == "Auto" ? L("Lyrics.Language_Auto") : x switch
        { "Chinese" => "中文", "English" => "English", "Japanese" => "日本語", "Korean" => "한국어", "French" => "Français", "Spanish" => "Español", "Russian" => "Русский", "German" => "Deutsch", "Portuguese" => "Português", _ => x }).ToArray();
    public string[] PerformanceLabels => [L("Lyrics.Profile_Saving"), L("Lyrics.Profile_Auto"), L("Lyrics.Profile_Precision")];
    public int LanguageIndex { get => Array.IndexOf(LyricsExtractionService.Languages, Options.Language); set { if (value >= 0 && value < LanguageLabels.Length) { Options.Language = LyricsExtractionService.Languages[value]; OnPropertyChanged(); } } }
    public int PerformanceIndex { get => (int)Options.Performance; set { if (value >= 0 && value <= 2) { Options.Performance = (LyricsPerformance)value; OnPropertyChanged(); OnPropertyChanged(nameof(ProfileHint)); } } }
    public string ProfileHint => L(Options.Performance switch { LyricsPerformance.ResourceSaving => "Lyrics.Hint_Saving", LyricsPerformance.HighPrecision => "Lyrics.Hint_Precision", _ => "Lyrics.Hint_Auto" });
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isScanning;
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PauseLabel))] private bool _isPaused;
    [ObservableProperty] private double _batchProgress;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _editorText = "";
    [ObservableProperty] private string _previewInfo = "";
    [ObservableProperty] private LyricsTaskItem? _selectedTask;
    public bool IsIdle => !IsBusy && !IsScanning;
    public bool HasPreview => SelectedTask?.Document != null;
    public string PauseLabel => L(IsPaused ? "Lyrics.Button_Resume" : "Lyrics.Button_Pause");
    public string QueueSummary => $"{Tasks.Count} {L("Lyrics.Label_Files")} · {Tasks.Count(t => t.Status == LyricsTaskStatus.Completed)} {L("Lyrics.Status_Completed")} · {Tasks.Count(t => t.Status == LyricsTaskStatus.Failed)} {L("Lyrics.Status_Failed")}";
    public bool Lrc { get => HasFormat(LyricsFormats.Lrc); set => Format(LyricsFormats.Lrc, value); }
    public bool Krc { get => HasFormat(LyricsFormats.Krc); set => Format(LyricsFormats.Krc, value); }
    public bool Qrc { get => HasFormat(LyricsFormats.Qrc); set => Format(LyricsFormats.Qrc, value); }
    public bool Srt { get => HasFormat(LyricsFormats.Srt); set => Format(LyricsFormats.Srt, value); }
    public bool Vtt { get => HasFormat(LyricsFormats.Vtt); set => Format(LyricsFormats.Vtt, value); }
    public bool Ass { get => HasFormat(LyricsFormats.Ass); set => Format(LyricsFormats.Ass, value); }
    public bool Yrc { get => HasFormat(LyricsFormats.Yrc); set => Format(LyricsFormats.Yrc, value); }
    public bool Text { get => HasFormat(LyricsFormats.Text); set => Format(LyricsFormats.Text, value); }
    public bool Json { get => HasFormat(LyricsFormats.Json); set => Format(LyricsFormats.Json, value); }
    public bool EnhancedLrc { get => HasFormat(LyricsFormats.EnhancedLrc); set => Format(LyricsFormats.EnhancedLrc, value); }
    private bool HasFormat(LyricsFormats value) => (Options.Formats & value) != 0;
    private void Format(LyricsFormats value, bool selected) { Options.Formats = selected ? Options.Formats | value : Options.Formats & ~value; }
    public static bool IsAudio(string file) => AudioExtensions.Contains(Path.GetExtension(file));
    private static string L(string key) => LocalizationRegistry.Get(key);

    public LyricsExtractionViewModel(LyricsExtractionService extraction, LyricsModelService models, IFileDialogService dialogs, ILogger<LyricsExtractionViewModel> logger)
    {
        _extraction = extraction; _models = models; _dialogs = dialogs; _logger = logger;
        string data = Environment.GetEnvironmentVariable("FANCYTOOL_LYRICS_DATA") ?? Path.Combine(AppPaths.DataDirectory, "Lyrics");
        Directory.CreateDirectory(data); _statePath = Path.Combine(data, "queue.json");
        Options.OutputDirectory = Path.Combine(data, "Output"); Options.ModelDirectory = LyricsModelService.DefaultDirectory;
        try
        {
            if (File.Exists(_statePath))
            {
                var state = JsonSerializer.Deserialize(File.ReadAllText(_statePath), LyricsJsonContext.Default.LyricsQueueState);
                if (state != null)
                {
                    Options = state.Options;
                    foreach (var item in state.Tasks.Where(t => File.Exists(t.SourceFile))) Tasks.Add(new(item.SourceFile, item.RootFolder)
                    { Status = item.Status == LyricsTaskStatus.Processing ? LyricsTaskStatus.Pending : item.Status, Error = item.Error, ResultPath = item.ResultPath, Stage = L("Lyrics.Status_" + (item.Status == LyricsTaskStatus.Processing ? LyricsTaskStatus.Pending : item.Status)) });
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Lyrics queue recovery failed"); StatusMessage = L("Lyrics.Error_QueueRecovery"); }
        I18nManager.Instance.CultureChanged += OnCultureChanged;
    }
    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(LanguageLabels)); OnPropertyChanged(nameof(PerformanceLabels)); OnPropertyChanged(nameof(ProfileHint)); OnPropertyChanged(nameof(PauseLabel)); OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(LanguageIndex)); OnPropertyChanged(nameof(PerformanceIndex));
        foreach (var item in Tasks.Where(t => t.Status != LyricsTaskStatus.Processing)) item.Stage = L("Lyrics.Status_" + item.Status);
    }
    partial void OnIsBusyChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged(); AddFilesCommand.NotifyCanExecuteChanged(); AddFolderCommand.NotifyCanExecuteChanged(); ClearCommand.NotifyCanExecuteChanged(); RemoveCommand.NotifyCanExecuteChanged(); PrepareModelsCommand.NotifyCanExecuteChanged();
        ExportEditedCommand.NotifyCanExecuteChanged(); RealignEditedCommand.NotifyCanExecuteChanged(); ImportLyricsCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsScanningChanged(bool value) => OnIsBusyChanged(IsBusy);
    partial void OnSelectedTaskChanged(LyricsTaskItem? value) { ListenCommand.NotifyCanExecuteChanged(); _ = LoadPreviewAsync(value); }
    private async Task LoadPreviewAsync(LyricsTaskItem? item)
    {
        EditorText = ""; PreviewInfo = "";
        if (item != null && item.Document == null && File.Exists(item.ResultPath))
        {
            try { item.Document = await Task.Run(() => LyricsFileService.Import(item.ResultPath, item.SourceFile, 0)); }
            catch (Exception ex) { StatusMessage = ex.Message; }
        }
        if (item != SelectedTask) return;
        RefreshPreview();
    }
    private void RefreshPreview()
    {
        if (SelectedTask?.Document is { } doc)
        {
            EditorText = LyricsFileService.ToEditor(doc);
            PreviewInfo = $"{doc.Language} · {doc.Lines.Count} {L("Lyrics.Label_Lines")} · {doc.Backend}\n" + string.Join("\n", doc.Warnings.Distinct().Select(L));
        }
        OnPropertyChanged(nameof(HasPreview)); ExportEditedCommand.NotifyCanExecuteChanged(); RealignEditedCommand.NotifyCanExecuteChanged(); ImportLyricsCommand.NotifyCanExecuteChanged();
    }
    private bool CanIdle() => IsIdle;
    private bool CanPreview() => IsIdle && HasPreview;
    [RelayCommand(CanExecute = nameof(CanIdle))]
    private async Task AddFilesAsync()
    {
        var files = await _dialogs.PickOpenFilesAsync(L("Lyrics.Button_AddFiles"), [new("Audio / Video") { Patterns = AudioExtensions.Select(x => "*" + x).ToArray() }]);
        if (files != null) await AddPathsAsync(files);
    }
    [RelayCommand(CanExecute = nameof(CanIdle))]
    private async Task AddFolderAsync() { string? folder = await _dialogs.PickFolderAsync(L("Lyrics.Button_AddFolder")); if (folder != null) await AddPathsAsync([folder]); }
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (!IsIdle) return;
        IsScanning = true;
        var known = Tasks.Select(t => t.SourceFile).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            var scanned = await Task.Run(() => BatchFileScanner.Scan(paths, IsAudio, known));
            Tasks.AddRange(scanned.Select(f => new LyricsTaskItem(f.File, f.Root) { Stage = L("Lyrics.Status_Pending") }));
            OnPropertyChanged(nameof(QueueSummary)); await SaveAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { IsScanning = false; }
    }
    [RelayCommand(CanExecute = nameof(CanIdle))]
    private async Task ClearAsync() { Tasks.Clear(); SelectedTask = null; OnPropertyChanged(nameof(QueueSummary)); await SaveAsync(); }
    [RelayCommand(CanExecute = nameof(CanIdle))]
    private async Task RemoveAsync() { if (SelectedTask != null) Tasks.Remove(SelectedTask); SelectedTask = Tasks.FirstOrDefault(); OnPropertyChanged(nameof(QueueSummary)); await SaveAsync(); }
    [RelayCommand] private async Task PickOutputAsync() { string? folder = await _dialogs.PickFolderAsync(L("Lyrics.Label_Output")); if (folder != null) { Options.OutputDirectory = folder; OnPropertyChanged(nameof(Options)); await SaveAsync(); } }
    [RelayCommand] private async Task PickModelsAsync() { string? folder = await _dialogs.PickFolderAsync(L("Lyrics.Label_Models")); if (folder != null) { Options.ModelDirectory = folder; OnPropertyChanged(nameof(Options)); await SaveAsync(); } }
    [RelayCommand] private void OpenOutput() { if (Directory.Exists(Options.OutputDirectory)) _dialogs.OpenInExplorer(Options.OutputDirectory); }
    private bool CanListen() => SelectedTask != null;
    [RelayCommand(CanExecute = nameof(CanListen))] private void Listen() { if (SelectedTask != null) try { Process.Start(new ProcessStartInfo(SelectedTask.SourceFile) { UseShellExecute = true }); } catch (Exception ex) { ShowError(ex); } }
    [RelayCommand] private void Cancel() { _cts?.Cancel(); _resume?.TrySetResult(); }
    [RelayCommand] private void TogglePause() { if (!CanPause) return; IsPaused = !IsPaused; if (IsPaused) _resume = new(TaskCreationOptions.RunContinuationsAsynchronously); else _resume?.TrySetResult(); }
    private Task WaitIfPaused(CancellationToken ct) => IsPaused && _resume != null ? _resume.Task.WaitAsync(ct) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanIdle))]
    private async Task PrepareModelsAsync()
    {
        BeginOperation();
        try { _models.Invalidate(); await _models.EnsureAsync(LyricsExtractionService.ModelDirectory(Options), Options.Performance == LyricsPerformance.HighPrecision, Progress(null), _cts!.Token); StatusMessage = L("Lyrics.Models_Ready"); }
        catch (Exception ex) { ShowError(ex); }
        finally { EndOperation(); await SaveAsync(); }
    }
    [RelayCommand(CanExecute = nameof(CanIdle))]
    private async Task StartAsync()
    {
        try { LyricsExtractionService.Validate(Options); if (string.IsNullOrWhiteSpace(Options.OutputDirectory)) throw new ArgumentException("Lyrics.Error_Output"); }
        catch (Exception ex) { ShowError(ex); return; }
        var queued = Tasks.Where(t => t.Status != LyricsTaskStatus.Completed).ToArray();
        if (queued.Length == 0) { StatusMessage = L("Lyrics.Hint_AddFiles"); return; }
        var options = JsonSerializer.Deserialize(JsonSerializer.Serialize(Options, LyricsJsonContext.Default.LyricsOptions), LyricsJsonContext.Default.LyricsOptions)!;
        foreach (var item in queued) { item.Status = LyricsTaskStatus.Pending; item.Stage = L("Lyrics.Status_Pending"); item.Error = ""; }
        BeginOperation(true); int completed = 0;
        try
        {
            await SaveAsync();
            foreach (var item in queued)
            {
                await WaitIfPaused(_cts!.Token); if (_cts.IsCancellationRequested) break;
                item.Status = LyricsTaskStatus.Processing; item.Error = ""; item.Progress = 0; await SaveAsync(); var timer = Stopwatch.StartNew();
                try
                {
                    var doc = await _extraction.ExtractAsync(item.SourceFile, options, Progress(item), _cts.Token, WaitIfPaused);
                    item.Stage = L("Lyrics.Stage_Export");
                    string basePath = UniqueBase(item, options.OutputDirectory);
                    item.ResultPath = await LyricsFileService.ExportAsync(doc, basePath, options.Formats, options.OffsetMs, _cts.Token);
                    item.Document = doc; item.Status = LyricsTaskStatus.Completed; item.Progress = 1; item.Stage = L("Lyrics.Status_Completed");
                    if (SelectedTask == null || SelectedTask == item) { SelectedTask = item; RefreshPreview(); }
                }
                catch (Exception) when (_cts.IsCancellationRequested) { item.Status = LyricsTaskStatus.Cancelled; item.Stage = L("Lyrics.Status_Cancelled"); break; }
                catch (Exception ex) { item.Status = LyricsTaskStatus.Failed; item.Error = TranslateError(ex); item.Stage = L("Lyrics.Status_Failed"); _logger.LogError(ex, "Lyrics extraction failed: {File}", item.SourceFile); }
                finally { item.Elapsed = timer.Elapsed.ToString(@"hh\:mm\:ss"); completed++; BatchProgress = (double)completed / queued.Length; OnPropertyChanged(nameof(QueueSummary)); await SaveAsync(); }
            }
            StatusMessage = QueueSummary;
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndOperation(); await SaveAsync(); }
    }
    public static string UniqueBase(LyricsTaskItem item, string output)
    {
        string relative = item.RootFolder == null ? Path.GetFileNameWithoutExtension(item.SourceFile) : Path.ChangeExtension(Path.GetRelativePath(item.RootFolder, item.SourceFile), null);
        if (relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative)) relative = Path.GetFileNameWithoutExtension(item.SourceFile);
        string original = Path.Combine(output, relative), candidate = original; int suffix = 1;
        // Check all supported sidecars, including originals when the user chooses the input folder.
        while (LyricsFileService.Extensions.Any(ext => File.Exists(candidate + ext)) || File.Exists(candidate + ".fancylyrics.json") || File.Exists(candidate + ".elrc")) candidate = original + $" ({suffix++})";
        return candidate;
    }
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private Task ExportEditedAsync() => SaveEditedAsync(false);
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private Task RealignEditedAsync() => SaveEditedAsync(true);
    private async Task SaveEditedAsync(bool realign)
    {
        var item = SelectedTask!; BeginOperation();
        try
        {
            var doc = LyricsFileService.FromEditor(EditorText, item.Document!);
            if (realign) { if (Options.Language != "Auto") doc.Language = Options.Language; await _extraction.RealignAsync(doc, Options, Progress(item), _cts!.Token); }
            string basePath = !string.IsNullOrEmpty(item.ResultPath) ? item.ResultPath[..^".fancylyrics.json".Length] : UniqueBase(item, Options.OutputDirectory);
            item.ResultPath = await LyricsFileService.ExportAsync(doc, basePath, Options.Formats, Options.OffsetMs, _cts!.Token); item.Document = doc;
            StatusMessage = L("Lyrics.Export_Saved"); RefreshPreview();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndOperation(); await SaveAsync(); }
    }
    private bool CanImport() => IsIdle && SelectedTask != null;
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportLyricsAsync()
    {
        string? path = await _dialogs.PickOpenFileAsync(L("Lyrics.Button_Import"), [new("Lyrics / Subtitles") { Patterns = LyricsFileService.Extensions.Select(e => "*" + e).ToArray() }]);
        if (path == null || SelectedTask == null) return;
        try
        {
            var item = SelectedTask;
            int duration = item.Document?.DurationMs ?? 0;
            if (duration == 0) duration = await Task.Run(() => (int)new ATL.Track(item.SourceFile).DurationMs);
            var document = await Task.Run(() => LyricsFileService.Import(path, item.SourceFile, duration));
            document.Language = Options.Language; item.Document = document;
            if (SelectedTask == item) RefreshPreview();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private IProgress<LyricsProgress> Progress(LyricsTaskItem? item) => new Progress<LyricsProgress>(p =>
    {
        if (!IsBusy) return;
        string text = L(p.Stage) + (string.IsNullOrEmpty(p.Detail) ? "" : " · " + p.Detail);
        StatusMessage = text; if (item != null) { item.Progress = p.Fraction; item.Stage = text; }
    });
    private void BeginOperation(bool allowPause = false) { CanPause = allowPause; IsBusy = true; IsPaused = false; _cts = new(); BatchProgress = 0; StatusMessage = ""; }
    private void EndOperation() { _resume?.TrySetResult(); IsPaused = false; CanPause = false; IsBusy = false; _cts?.Dispose(); _cts = null; }
    private static string TranslateError(Exception ex) => ex is OperationCanceledException ? L("Lyrics.Status_Cancelled") : ex.Message.StartsWith("Lyrics.", StringComparison.Ordinal) ? L(ex.Message) : ex.Message;
    private void ShowError(Exception ex) { StatusMessage = TranslateError(ex); _logger.LogWarning(ex, "Lyrics operation failed"); }
    private async Task SaveAsync()
    {
        string json = JsonSerializer.Serialize(new LyricsQueueState { Options = Options, Tasks = Tasks.Select(t => new LyricsTaskRecord { SourceFile = t.SourceFile, RootFolder = t.RootFolder, Status = t.Status, Error = t.Error, ResultPath = t.ResultPath }).ToList() }, LyricsJsonContext.Default.LyricsQueueState);
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try { await File.WriteAllTextAsync(_statePath + ".tmp", json).ConfigureAwait(false); File.Move(_statePath + ".tmp", _statePath, true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Lyrics queue could not be saved"); Dispatcher.UIThread.Post(() => StatusMessage = L("Lyrics.Error_QueueSave")); }
        finally { _saveGate.Release(); }
    }
    public void Dispose() { I18nManager.Instance.CultureChanged -= OnCultureChanged; _cts?.Cancel(); _resume?.TrySetResult(); SaveAsync().GetAwaiter().GetResult(); }
}
