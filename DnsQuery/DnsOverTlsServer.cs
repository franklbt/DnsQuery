using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using static DnsQuery.Helpers;

namespace DnsQuery;

public class DnsOverTlsServer(IConfiguration configuration, IPEndPoint baseDnsServer)
{
    private readonly TcpListener _listener = new(IPAddress.Any, 853);

    private readonly X509Certificate2 _serverCertificate = X509CertificateLoader.LoadPkcs12FromFile(
        configuration.GetValue<string>("CertificatePath")!,
        configuration.GetValue<string>("CertificatePassword")!);


    public async Task StartAsync()
    {
        _listener.Start();
        Console.WriteLine("Serveur DoT démarré sur le port 853.");

        while (true)
        {
            var tcpClient = await _listener.AcceptTcpClientAsync();
            _ = HandleClientAsync(tcpClient);
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient)
    {
        using (tcpClient)
        {
            try
            {
                var sslStream = new SslStream(tcpClient.GetStream(), false);
                await sslStream.AuthenticateAsServerAsync(_serverCertificate, clientCertificateRequired: false,
                    checkCertificateRevocation: false);
                await ProcessDnsQueriesAsync(sslStream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur lors de la gestion du client : {ex.Message}");
            }
        }
    }

    private async Task ProcessDnsQueriesAsync(SslStream sslStream)
    {
        while (true)
        {
            using var lengthOwner = MemoryPool<byte>.Shared.Rent(2);
            var lengthBuffer = lengthOwner.Memory.Slice(0, 2);
            var bytesRead = await sslStream.ReadAsync(lengthBuffer);
            if (bytesRead == 0)
            {
                break;
            }

            if (bytesRead != 2)
            {
                Console.WriteLine("Préfixe de longueur invalide.");
                break;
            }

            var messageLength = (lengthBuffer.Span[0] << 8) | lengthBuffer.Span[1];

            using var messageOwner = MemoryPool<byte>.Shared.Rent(messageLength);
            var messageBuffer = messageOwner.Memory.Slice(0, messageLength);
            bytesRead = await sslStream.ReadAsync(messageBuffer);
            if (bytesRead != messageLength)
            {
                Console.WriteLine("Message DNS incomplet reçu.");
                break;
            }

            var responseMessage = await ResolveDnsAsync(messageBuffer, baseDnsServer);

            using var responseLengthOwner = MemoryPool<byte>.Shared.Rent(2);
            var responseLengthBuffer = responseLengthOwner.Memory.Slice(0, 2);
            responseLengthBuffer.Span[0] = (byte)(responseMessage.Length >> 8);
            responseLengthBuffer.Span[1] = (byte)(responseMessage.Length & 0xFF);

            await sslStream.WriteAsync(responseLengthBuffer);
            await sslStream.WriteAsync(responseMessage);
        }
    }
}
