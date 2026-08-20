namespace LittleFancyToolAva.ViewModels
{
    public partial class CommToolViewModel : ViewModelBase
    {
        public TcpServerViewModel TcpVm { get; }

        public UdpViewModel UdpVm { get; }

        public SerialPortViewModel SerialVm { get; }

        public CommToolViewModel(TcpServerViewModel tcp, UdpViewModel udp, SerialPortViewModel serial)
        {
            TcpVm = tcp;
            UdpVm = udp;
            SerialVm = serial;
        }
    }
}