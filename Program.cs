using Avalonia;
using Serilog;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LittleFancyToolAva
{
    internal sealed class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

        [STAThread]
        public static void Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            try
            {
                System.IO.Directory.CreateDirectory(LittleFancyToolAva.Services.AppPaths.DataDirectory);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to create data directory: {ex.Message}");
            }

            string logDir = System.IO.Path.Combine(LittleFancyToolAva.Services.AppPaths.DataDirectory, "logs");
            try
            {
                System.IO.Directory.CreateDirectory(logDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to create log directory: {ex.Message}");
            }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    System.IO.Path.Combine(logDir, "tool-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    fileSizeLimitBytes: 50 * 1024 * 1024,
                    shared: true)
                .CreateLogger();

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Log.Fatal(ex, "Unhandled domain exception (terminating={IsTerminating})", e.IsTerminating);
                ShowFatalError(ex?.Message ?? "Unknown error");
            };

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Error(e.Exception, "Unobserved task exception");
            };

            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application startup failed");
                ShowFatalError(ex.Message);
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static void ShowFatalError(string message)
        {
            if (!OperatingSystem.IsWindows())
            {
                Log.Error("Fatal error on non-Windows platform: {Message}", message);
                return;
            }

            try
            {
                MessageBoxW(0, message, "Fatal Error", 0x00000010u);
            }
            catch
            {
                // 全局兜底已尽力
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
