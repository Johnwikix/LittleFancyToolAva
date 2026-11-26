using System.IO.Ports;

namespace FancyToolAva.Services
{
    /// <summary>
    /// 串口设备目录：端口枚举只有一处实现，会话与 Hub 共用，避免每个会话各自枚举。
    /// </summary>
    public static class SerialPortCatalog
    {
        public static string[] GetPortNames()
        {
            try
            {
                return SerialPort.GetPortNames();
            }
            catch
            {
                return [];
            }
        }
    }
}