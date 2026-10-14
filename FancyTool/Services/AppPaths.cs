namespace FancyToolAva.Services
{
    public static class AppPaths
    {
        // Use LocalApplicationData (a.k.a. %LocalAppData%) rather than the binary's
        // base directory. The MSIX VFS layer transparently redirects LocalAppData
        // to the package's LocalCache, which is writable and persists across
        // app upgrades. Plain .exe installations get %LocalAppData%\FancyTool,
        // which avoids the "Program Files is read-only" failure mode.
        public static string DataDirectory { get; } = Environment.GetEnvironmentVariable("FANCYTOOL_DATA_DIRECTORY") ??
            (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FancyTool")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fancy-tool"));

        public static string DefaultModelsDirectory => Path.Combine(DataDirectory, "Models");

        private static string? _modelsDirectory;
        public static string ModelsDirectory => _modelsDirectory ?? DefaultModelsDirectory;

        public static string ResolveModelsDirectory(string? directory) => string.IsNullOrWhiteSpace(directory)
            ? DefaultModelsDirectory
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));

        // Capture the root at startup. Downloads and inference sessions keep using
        // the same directory until restart, even when settings are changed mid-batch.
        public static void InitializeModelsDirectory(string? directory) =>
            _modelsDirectory = ResolveModelsDirectory(directory);

        // Where the previous version of the app stored its config (next to the
        // binary). We check this once on first launch under the new layout and
        // migrate preferences.json so existing users keep their settings.
        public static string LegacyDataDirectory =>
            Path.Combine(AppContext.BaseDirectory, "Assets");

        public static string LegacyPreferencesPath =>
            Path.Combine(AppContext.BaseDirectory, "preferences.json");

        static AppPaths()
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                MigrateLegacyPreferences();
            }
            catch
            {
                // best-effort; never block startup on migration issues
            }
        }

        private static void MigrateLegacyPreferences()
        {
            string target = Path.Combine(DataDirectory, "preferences.json");
            if (File.Exists(target)) return;
            string legacy = LegacyPreferencesPath;
            if (!File.Exists(legacy)) return;
            try
            {
                File.Copy(legacy, target, overwrite: false);
            }
            catch
            {
                // ignore — preferences will simply reset to defaults
            }
        }
    }
}
