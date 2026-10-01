using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FancyToolAva.Models;

public enum LyricsPerformance { ResourceSaving, Automatic, HighPrecision }
public enum LyricsTaskStatus { Pending, Processing, Completed, Failed, Cancelled }
[Flags] public enum LyricsFormats { Lrc = 1, Krc = 2, Qrc = 4, Srt = 8, Vtt = 16, Ass = 32, Yrc = 64, Text = 128, Json = 256, EnhancedLrc = 512 }

public sealed class LyricsOptions
{
    public string Language { get; set; } = "Auto";
    public LyricsPerformance Performance { get; set; } = LyricsPerformance.Automatic;
    public bool UseGpu { get; set; } = true;
    public bool SongMode { get; set; } = true;
    public bool PreferExisting { get; set; } = true;
    public string OutputDirectory { get; set; } = "";
    public string ModelDirectory { get; set; } = "";
    public LyricsFormats Formats { get; set; } = LyricsFormats.Lrc | LyricsFormats.Srt;
    public int ChunkSeconds { get; set; } = 30;
    public int MaxLineCharacters { get; set; } = 42;
    public int OffsetMs { get; set; }
}

public sealed class LyricsWord
{
    public string Text { get; set; } = "";
    public int StartMs { get; set; }
    public int EndMs { get; set; }
}
public sealed class LyricsLine
{
    public string Text { get; set; } = "";
    public int StartMs { get; set; }
    public int EndMs { get; set; }
    public List<LyricsWord> Words { get; set; } = [];
}
public sealed class LyricsDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string SourceFile { get; set; } = "";
    public string Language { get; set; } = "Auto";
    public string Model { get; set; } = "Qwen3-ASR-1.7B";
    public string Backend { get; set; } = "CPU";
    public int DurationMs { get; set; }
    public List<LyricsLine> Lines { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
}
public sealed record LyricsProgress(string Stage, double Fraction, string Detail = "");
public sealed class LyricsTaskRecord
{
    public string SourceFile { get; set; } = "";
    public string? RootFolder { get; set; }
    public LyricsTaskStatus Status { get; set; }
    public string Error { get; set; } = "";
    public string ResultPath { get; set; } = "";
}
public sealed class LyricsQueueState
{
    public LyricsOptions Options { get; set; } = new();
    public List<LyricsTaskRecord> Tasks { get; set; } = [];
}
public partial class LyricsTaskItem : ObservableObject
{
    public string SourceFile { get; }
    public string FileName => Path.GetFileName(SourceFile);
    public string? RootFolder { get; }
    [ObservableProperty] private LyricsTaskStatus _status;
    [ObservableProperty] private string _stage = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _resultPath = "";
    [ObservableProperty] private string _elapsed = "";
    public LyricsDocument? Document { get; set; }
    public LyricsTaskItem(string sourceFile, string? rootFolder = null) { SourceFile = sourceFile; RootFolder = rootFolder; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LyricsDocument))]
[JsonSerializable(typeof(LyricsQueueState))]
[JsonSerializable(typeof(LyricsOptions))]
[JsonSerializable(typeof(List<Services.ModelManifestEntry>))]
internal partial class LyricsJsonContext : JsonSerializerContext;
