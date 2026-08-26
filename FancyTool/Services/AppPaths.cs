namespace FancyToolAva.Services
{
    public static class AppPaths
    {
        // Use LocalApplicationData (a.k.a. %LocalAppData%) rather than the binary's
        // base directory. The MSIX VFS layer transparently redirects LocalAppData
        // to the package's LocalCache, which is writable and persists across
        // app upgrades. Plain .exe installations get %LocalAppData%\FancyTool,
        // which avoids the "Program Files is read-only" failure mode.
        public static string DataDirectory { get; } =
            OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FancyTool")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fancy-tool");

        public static string ModelsDirectory => Path.Combine(DataDirectory, "Models");

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