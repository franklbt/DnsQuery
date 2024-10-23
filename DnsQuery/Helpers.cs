using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DnsQuery;

public static class Helpers
{
    
    public static byte[] CreateDnsQuery(string domainName)
    {
        var random = new Random();
        ushort transactionId = (ushort)random.Next(0, ushort.MaxValue);

        var dnsQuery = new MemoryStream();
        using (var writer = new BinaryWriter(dnsQuery))
        {
            writer.Write((ushort)IPAddress.HostToNetworkOrder((short)transactionId));
            writer.Write((ushort)IPAddress.HostToNetworkOrder((short)0x0100));
            writer.Write((ushort)IPAddress.HostToNetworkOrder((short)1));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);

            string[] labels = domainName.Split('.');
            foreach (string label in labels)
            {
                byte length = (byte)label.Length;
                writer.Write(length);
                writer.Write(Encoding.ASCII.GetBytes(label));
            }
            writer.Write((byte)0);
            writer.Write((ushort)IPAddress.HostToNetworkOrder((short)1));
            writer.Write((ushort)IPAddress.HostToNetworkOrder((short)1));
        }

        return dnsQuery.ToArray();
    }

    public static byte[] FromBase64Url(string base64Url)
    {
        string padded = base64Url.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    public static async Task<byte[]> ResolveDnsAsync(byte[] dnsRequest, string customDns)
    {
        using var udpClient = new UdpClient();
        var dnsServerEndpoint = new IPEndPoint(IPAddress.Parse(customDns), 53);
        await udpClient.SendAsync(dnsRequest, dnsRequest.Length, dnsServerEndpoint);
        var udpReceiveResult = await udpClient.ReceiveAsync();
        return udpReceiveResult.Buffer;
    }

}
