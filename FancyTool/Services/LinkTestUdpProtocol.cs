using System.Buffers.Binary;

namespace FancyToolAva.Services;

/// <summary>
/// 链路测试 UDP 数据包协议（所有多字节整数均为小端）。
/// <code>
/// DATA / FIN 包布局（HeaderSize = 16）：
///   [0..3]  Magic  "FTLT"（0x46 0x54 0x4C 0x54）
///   [4..7]  Flags  0 = DATA, 1 = FIN, 2 = FIN-ACK
///   [8..15] Seq    DATA：包序号（从 0 起）；FIN：实际发送的包总数
/// FIN-ACK 载荷（接收端 → 发送端，共 40 字节）：
///   [16..23] 接收到的包数
///   [24..31] 接收到的字节数
///   [32..39] 收到的最大序号
/// </code>
/// 非 Data 包以外的普通 UDP 流量会被魔数校验直接忽略，
/// 因此链路测试与普通 UDP 调试即使共用端口也不会互相干扰统计。
/// </summary>
internal static class LinkTestUdpProtocol
{
    public const int HeaderSize = 16;
    public const int FinAckPayloadSize = 24;
    public const int FinAckPacketSize = HeaderSize + FinAckPayloadSize;

    public const byte FlagData = 0;
    public const byte FlagFin = 1;
    public const byte FlagFinAck = 2;

    /// <summary>UDP 链路测试允许的最小包长（含协议头）。</summary>
    public const int MinPacketSize = 64;
    /// <summary>以太网 MTU 1500 − IP 头 20 − UDP 头 8，避免 IP 分片。</summary>
    public const int MaxPacketSize = 1472;

    /// <summary>接收端收到 FIN 后继续收尾巴包的时间，容忍乱序迟到。</summary>
    public const int FinTailMs = 500;
    /// <summary>发送端等待对端 FIN-ACK 的超时。</summary>
    public const int FinAckTimeoutMs = 2000;
    /// <summary>接收端收到数据后连续无包的静默超时（未收到 FIN 时自动结束）。</summary>
    public const int IdleTimeoutMs = 15000;
    /// <summary>接收端等待首包的超时。</summary>
    public const int FirstPacketTimeoutMs = 60000;
    /// <summary>接收端 Socket 收包超时，用于周期检查取消与超时。</summary>
    public const int ReceiveTimeoutMs = 500;
    /// <summary>接收端建议的接收缓冲区大小，减少内核层溢出丢包。</summary>
    public const int ReceiveBufferBytes = 8 * 1024 * 1024;

    /// <summary>FIN-ACK 重发次数（UDP 无保障，重复发送以提高送达率）。</summary>
    public const int FinAckRepeatCount = 3;
    public const int FinAckRepeatIntervalMs = 20;

    public static bool TryReadHeader(ReadOnlySpan<byte> packet, out byte flags, out long seq)
    {
        flags = 0;
        seq = 0;
        if (packet.Length < HeaderSize) return false;
        if (packet[0] != 0x46 || packet[1] != 0x54 || packet[2] != 0x4C || packet[3] != 0x54) return false;
        flags = packet[4];
        seq = BinaryPrimitives.ReadInt64LittleEndian(packet.Slice(8, 8));
        return true;
    }

    public static void WriteHeader(Span<byte> packet, byte flags, long seq)
    {
        packet[0] = 0x46;
        packet[1] = 0x54;
        packet[2] = 0x4C;
        packet[3] = 0x54;
        packet[4] = flags;
        packet[5] = 0;
        packet[6] = 0;
        packet[7] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(packet.Slice(8, 8), seq);
    }

    public static void WriteFinAck(Span<byte> packet, long receivedPackets, long receivedBytes, long maxSeq)
    {
        WriteHeader(packet, FlagFinAck, 0);
        var payload = packet.Slice(HeaderSize, FinAckPayloadSize);
        BinaryPrimitives.WriteInt64LittleEndian(payload.Slice(0, 8), receivedPackets);
        BinaryPrimitives.WriteInt64LittleEndian(payload.Slice(8, 8), receivedBytes);
        BinaryPrimitives.WriteInt64LittleEndian(payload.Slice(16, 8), maxSeq);
    }

    public static bool TryReadFinAck(ReadOnlySpan<byte> packet, out long receivedPackets, out long receivedBytes, out long maxSeq)
    {
        receivedPackets = 0;
        receivedBytes = 0;
        maxSeq = 0;
        if (packet.Length < FinAckPacketSize) return false;
        if (!TryReadHeader(packet, out var flags, out _) || flags != FlagFinAck) return false;
        var payload = packet.Slice(HeaderSize, FinAckPayloadSize);
        receivedPackets = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(0, 8));
        receivedBytes = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(8, 8));
        maxSeq = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(16, 8));
        return true;
    }
}
