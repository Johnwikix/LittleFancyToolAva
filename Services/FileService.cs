using LittleFancyToolAva.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LittleFancyToolAva.Services
{
    public class FileService
    {
        private readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "preferences.json");

        public class PersistedState
        {
            public AppPreferences? Preferences { get; set; }
        }

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            TypeInfoResolver = PersistedStateJsonContext.Default
        };

        public void SaveState(AppObserveModel model)
        {
            var state = new PersistedState
            {
                Preferences = model.Preferences
            };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(state, PersistedStateJsonContext.Default.PersistedState));
        }

        public PersistedState? LoadState()
        {
            if (!File.Exists(_filePath)) return null;
            try
            {
                string json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<PersistedState>(json, PersistedStateJsonContext.Default.PersistedState);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileService] Load failed: {ex.Message}");
                return null;
            }
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(FileService.PersistedState))]
    [JsonSerializable(typeof(AppPreferences))]
    internal partial class PersistedStateJsonContext : JsonSerializerContext
    {
    }
}
