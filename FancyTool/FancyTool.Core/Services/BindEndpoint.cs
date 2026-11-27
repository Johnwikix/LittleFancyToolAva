using System.Net;

namespace FancyToolAva.Services
{
    /// <summary>
    /// 绑定端点（地址 + 端口）。服务层用它决定实际绑定的地址，页面层用它判定会话之间的冲突；
    /// 两边走同一个解析，避免"判定的地址"和"实际绑定的地址"不是同一个。
    /// </summary>
    public readonly struct BindEndpoint : IEquatable<BindEndpoint>
    {
        public IPAddress Address { get; }
        public int Port { get; }

        private BindEndpoint(IPAddress address, int port)
        {
            Address = address;
            Port = port;
        }

        /// <summary>TCP 绑定端点：地址无法解析时回退环回地址，与 <c>TcpServerService</c> 现状一致。</summary>
        public static BindEndpoint ForTcp(string? address, int port) => new(Resolve(address, IPAddress.Loopback), port);

        /// <summary>UDP 绑定端点：地址无法解析时回退任意地址，与 <c>UdpService</c> 现状一致。</summary>
        public static BindEndpoint ForUdp(string? address, int port) => new(Resolve(address, IPAddress.Any), port);

        private static IPAddress Resolve(string? address, IPAddress fallback)
            => IPAddress.TryParse(address, out var parsed) ? parsed : fallback;

        /// <summary>
        /// 两个绑定端点是否互斥：端口相同，且地址相同或任一方是通配地址。
        /// 双栈下 <c>[::]</c> 同时覆盖 IPv4，按最宽的情况算；IPv4 通配只覆盖 IPv4。
        /// </summary>
        public bool Overlaps(BindEndpoint other)
        {
            if (Port != other.Port) return false;

            IPAddress a = Normalize(Address);
            IPAddress b = Normalize(other.Address);
            if (a.Equals(b)) return true;
            if (a.Equals(IPAddress.IPv6Any) || b.Equals(IPAddress.IPv6Any)) return true;
            return a.AddressFamily == b.AddressFamily && (a.Equals(IPAddress.Any) || b.Equals(IPAddress.Any));
        }

        private static IPAddress Normalize(IPAddress address)
            => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        public bool Equals(BindEndpoint other) => Port == other.Port && Address.Equals(other.Address);

        public override bool Equals(object? obj) => obj is BindEndpoint other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Address, Port);

        public override string ToString() => $"{Address}:{Port}";
    }

    /// <summary>会话之间的端点冲突规则。传入的集合只应包含运行中或启动中的会话。</summary>
    public static class EndpointConflict
    {
        /// <summary>TCP：只有服务端会话互斥，客户端会话不参与。</summary>
        public static bool Tcp(BindEndpoint candidate, IEnumerable<BindEndpoint> activeServers)
            => activeServers.Any(e => e.Overlaps(candidate));

        /// <summary>
        /// UDP：普通会话与任何会话互斥；组播会话之间放行（同一组各收一份是合法用法），只与普通会话互斥。
        /// UDP 允许同端口重复绑定，投递行为未定义，所以必须在应用层拦截。
        /// </summary>
        public static bool Udp(BindEndpoint candidate, bool candidateIsMulticast,
            IEnumerable<(BindEndpoint Endpoint, bool IsMulticast)> active)
            => active.Any(o => (!candidateIsMulticast || !o.IsMulticast) && o.Endpoint.Overlaps(candidate));
    }
}
