using LittleFancyToolAva.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LittleFancyToolAva.Services
{
    public class FileService
    {
        private readonly string _filePath;
        private readonly string _backupPath;
        private readonly ILogger<FileService> _logger;

        public class PersistedState
        {
            public AppPreferences? Preferences { get; set; }
        }

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            TypeInfoResolver = PersistedStateJsonContext.Default,
            MaxDepth = 64
        };

        public FileService(ILogger<FileService> logger)
        {
            _logger = logger;
            _filePath = Path.Combine(AppContext.BaseDirectory, "preferences.json");
            _backupPath = _filePath + ".bak";
        }

        public void SaveState(AppObserveModel model)
        {
            var state = new PersistedState
            {
                Preferences = model.Preferences
            };
            string json = JsonSerializer.Serialize(state, PersistedStateJsonContext.Default.PersistedState);
            AtomicWrite(_filePath, json);
        }

        public PersistedState? LoadState()
        {
            if (!File.Exists(_filePath))
            {
                if (File.Exists(_backupPath))
                {
                    _logger.LogInformation("Primary preferences not found, loading backup");
                    return TryLoadFrom(_backupPath);
                }
                return null;
            }
            try
            {
                var fi = new FileInfo(_filePath);
                if (fi.Length > 5 * 1024 * 1024)
                {
                    _logger.LogWarning("Preferences file too large ({Size} bytes), using defaults", fi.Length);
                    return null;
                }
                var result = TryLoadFrom(_filePath);
                if (result != null)
                {
                    try { File.Copy(_filePath, _backupPath, overwrite: true); } catch { /* 备份失败不影响主流程 */ }
                }
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load preferences, trying backup");
                var backupResult = TryLoadFrom(_backupPath);
                if (backupResult != null)
                    _logger.LogInformation("Recovered preferences from backup");
                return backupResult;
            }
        }

        private PersistedState? TryLoadFrom(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<PersistedState>(json, PersistedStateJsonContext.Default.PersistedState);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "JSON parse error in {Path}", path);
                return null;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "IO error reading {Path}", path);
                return null;
            }
        }

        private static void AtomicWrite(string path, string content)
        {
            string tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, content);
            File.Move(tmpPath, path, overwrite: true);
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(FileService.PersistedState))]
    [JsonSerializable(typeof(AppPreferences))]
    internal partial class PersistedStateJsonContext : JsonSerializerContext
    {
    }
}
