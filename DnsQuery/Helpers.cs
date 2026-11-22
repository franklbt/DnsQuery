using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DnsQuery;

public static class Helpers
{
    public static byte[] CreateDnsQuery(string domainName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainName);
        var labels = domainName.TrimEnd('.').Split('.');
        if (labels.Length == 0 || labels.Any(label => label.Length is 0 or > 63))
            throw new ArgumentException("The domain name is invalid.", nameof(domainName));

        var encodedLength = labels.Sum(label => Encoding.ASCII.GetByteCount(label) + 1);
        if (encodedLength > 254)
            throw new ArgumentException("The domain name is too long.", nameof(domainName));

        var query = new byte[DnsMessage.HeaderLength + encodedLength + 5];
        BinaryPrimitives.WriteUInt16BigEndian(query, (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1));
        query[2] = 0x01;
        query[5] = 0x01;
        var offset = DnsMessage.HeaderLength;
        foreach (var label in labels)
        {
            query[offset++] = (byte)label.Length;
            offset += Encoding.ASCII.GetBytes(label, query.AsSpan(offset));
        }

        query[offset++] = 0;
        query[offset + 1] = 1; // QTYPE=A
        query[offset + 3] = 1; // QCLASS=IN
        return query;
    }

    public static bool TryDecodeBase64Url(string encoded, int maximumDecodedLength, out byte[] decoded)
    {
        decoded = [];
        if (string.IsNullOrEmpty(encoded) || encoded.Length > ((maximumDecodedLength + 2) / 3 * 4))
            return false;

        var paddedLength = (encoded.Length + 3) & ~3;
        var chars = ArrayPool<char>.Shared.Rent(paddedLength);
        var decodedCapacity = Math.Min(maximumDecodedLength, paddedLength / 4 * 3);
        var bytes = ArrayPool<byte>.Shared.Rent(Math.Max(decodedCapacity, 1));
        try
        {
            encoded.AsSpan().CopyTo(chars);
            for (var i = 0; i < encoded.Length; i++)
            {
                chars[i] = chars[i] switch
                {
                    '-' => '+',
                    '_' => '/',
                    _ => chars[i]
                };
            }
            chars.AsSpan(encoded.Length, paddedLength - encoded.Length).Fill('=');

            if (!Convert.TryFromBase64Chars(chars.AsSpan(0, paddedLength), bytes, out var written) ||
                written > maximumDecodedLength)
                return false;

            decoded = bytes.AsSpan(0, written).ToArray();
            return true;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }
}
