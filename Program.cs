using Avalonia;
using System;
using System.Runtime.InteropServices;

namespace LittleFancyToolAva
{
    internal sealed class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

        [STAThread]
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Console.Error.WriteLine($"FATAL: {ex}");
                ShowFatalError(ex?.Message ?? "未知错误");
            };

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Console.Error.WriteLine($"UNOBSERVED TASK: {e.Exception}");
                e.SetObserved();
            };

            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FATAL: {ex}");
                ShowFatalError(ex.Message);
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
