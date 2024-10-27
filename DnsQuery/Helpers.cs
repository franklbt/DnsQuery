using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DnsQuery;

public static class Helpers
{
    public static UdpClientPool UdpClientPool { get; } = new();
    public static byte[] CreateDnsQuery(string domainName)
    {
        var random = new Random();
        var transactionId = (ushort)random.Next(0, ushort.MaxValue);

        var dnsQuery = new MemoryStream();
        using (var writer = new BinaryWriter(dnsQuery))
        {
            writer.Write((ushort)IPAddress.HostToNetworkOrder((short)transactionId));
            writer.Write((ushort)IPAddress.HostToNetworkOrder((short)0x0100));
            writer.Write((ushort)IPAddress.HostToNetworkOrder((short)1));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);

            var labels = domainName.Split('.');
            foreach (var label in labels)
            {
                var length = (byte)label.Length;
                writer.Write(length);
                writer.Write(Encoding.ASCII.GetBytes(label));
            }
            writer.Write((byte)0);
            writer.Write((ushort)IPAddress.HostToNetworkOrder((short)1));
            writer.Write((ushort)IPAddress.HostToNetworkOrder((short)1));
        }

        return dnsQuery.ToArray();
    }

    public static Memory<byte> FromBase64Url(string base64Url)
    {
        if(base64Url.Length == 0)
            return Memory<byte>.Empty;
        
        var paddingNeeded = (4 - base64Url.Length % 4) % 4;
        var totalLength = base64Url.Length + paddingNeeded;   
        using var paddedSpanOwner = MemoryPool<char>.Shared.Rent(totalLength);
        var paddedSpan = paddedSpanOwner.Memory[..totalLength].Span;
        
        base64Url.AsSpan().CopyTo(paddedSpan);
        for (var i = 0; i < paddedSpan.Length; i++)
        {
            if (paddedSpan[i] == '-') 
                paddedSpan[i] = '+';
            else if (paddedSpan[i] == '_') 
                paddedSpan[i] = '/';
        }
        for (var i = base64Url.Length; i < totalLength; i++)
        {
            paddedSpan[i] = '=';
        }
        
        var expectedDecodedLength = base64Url.Length * 3 / 4;
        using var decodedBufferOwner = MemoryPool<byte>.Shared.Rent(expectedDecodedLength);
        var decodedBufferMemory = decodedBufferOwner.Memory[..expectedDecodedLength];
        var decodedBuffer = decodedBufferMemory.Span;

        if (Convert.TryFromBase64Chars(paddedSpan, decodedBuffer, out var _))
            return decodedBufferMemory;
        
        return Memory<byte>.Empty;
    }

    public static async Task<Memory<byte>> ResolveDnsAsync(ReadOnlyMemory<byte> dnsRequest, IPEndPoint dnsServerEndpoint)
    {
        var udpClient = UdpClientPool.Rent();

        try
        { 
            udpClient.Client.ReceiveTimeout = 5000;
            await udpClient.SendAsync(dnsRequest, dnsServerEndpoint);
            var udpReceiveResult = await udpClient.ReceiveAsync();
            return udpReceiveResult.Buffer.AsMemory();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            throw;
        }
        finally
        {
            UdpClientPool.Return(udpClient);
        }
    }

}
