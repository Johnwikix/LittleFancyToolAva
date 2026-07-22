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

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "LittleFancyToolAva", "logs", "tool-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    fileSizeLimitBytes: 50 * 1024 * 1024,
                    shared: true)
                .CreateLogger();

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Log.Fatal(ex, "Unhandled domain exception (terminating={IsTerminating})", e.IsTerminating);
                ShowFatalError(ex?.Message ?? "未知错误");
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
            try
            {
                MessageBoxW(0, message, "致命错误", 0x00000010u);
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
