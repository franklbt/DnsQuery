using System.Buffers.Binary;

namespace DnsQuery;

public static class DnsMessage
{
    public const int HeaderLength = 12;

    public static bool IsQuery(ReadOnlySpan<byte> message) =>
        message.Length >= HeaderLength && (message[2] & 0x80) == 0;

    public static bool IsResponse(ReadOnlySpan<byte> message) =>
        message.Length >= HeaderLength && (message[2] & 0x80) != 0;

    public static bool IsTruncated(ReadOnlySpan<byte> message) =>
        message.Length >= HeaderLength && (message[2] & 0x02) != 0;

    public static ushort ReadId(ReadOnlySpan<byte> message) =>
        BinaryPrimitives.ReadUInt16BigEndian(message);

    public static void WriteId(Span<byte> message, ushort id) =>
        BinaryPrimitives.WriteUInt16BigEndian(message, id);
}
