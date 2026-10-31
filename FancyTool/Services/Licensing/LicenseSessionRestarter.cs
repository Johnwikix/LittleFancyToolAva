using System.Diagnostics;
using Serilog;

namespace FancyToolAva.Services;

/// <summary>运行中撤权的进程边界；即使锁定、保存或重新启动失败，也必须结束所有旧工具任务。</summary>
internal static class LicenseSessionRestarter
{
    public static void Restart(Action lockWindow, Action saveState)
    {
        try
        {
            try { lockWindow(); }
            catch (Exception ex) { Log.Warning(ex, "隐藏失效许可的工具窗口失败"); }
            try { saveState(); }
            catch (Exception ex) { Log.Warning(ex, "授权失效后保存状态失败"); }
            try
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位应用程序。");
                using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                if (process == null) Log.Error("无法重新打开授权锁定页");
            }
            catch (Exception ex) { Log.Error(ex, "无法重新打开授权锁定页，请重新启动应用购买或验证许可"); }
        }
        finally
        {
            // 不在 UI 线程同步释放可能等待原生任务的服务；OS 回收全部原生句柄。
            // 重新启动不传递跳过授权的参数；正在写入的输出可能不完整。
            try { Log.CloseAndFlush(); }
            finally { Environment.Exit(0); }
        }
    }
}
