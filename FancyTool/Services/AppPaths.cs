namespace FancyToolAva.Services
{
    public static class AppPaths
    {
        public static string DataDirectory { get; } =
            OperatingSystem.IsWindows()
                ? AppContext.BaseDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "fancy-tool");
    }
}
