using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using static DnsQuery.Helpers;

namespace DnsQuery;

public class DnsOverTlsServer(IConfiguration configuration)
{
    private readonly TcpListener _listener = new(IPAddress.Any, 853);

    private readonly X509Certificate2 _serverCertificate = X509CertificateLoader.LoadPkcs12FromFile(
        configuration.GetValue<string>("CertificatePath")!,
        configuration.GetValue<string>("CertificatePassword")!);
    
    private readonly string _baseDnsServer = configuration.GetValue<string>("BaseDnsServer")!;


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
            var lengthBuffer = new byte[2];
            var bytesRead = await sslStream.ReadAsync(lengthBuffer, 0, 2);
            if (bytesRead == 0)
            {
                break;
            }

            if (bytesRead != 2)
            {
                Console.WriteLine("Préfixe de longueur invalide.");
                break;
            }

            var messageLength = (lengthBuffer[0] << 8) | lengthBuffer[1];

            var messageBuffer = new byte[messageLength];
            bytesRead = await sslStream.ReadAsync(messageBuffer, 0, messageLength);
            if (bytesRead != messageLength)
            {
                Console.WriteLine("Message DNS incomplet reçu.");
                break;
            }

            var responseMessage = await ResolveDnsAsync(messageBuffer, _baseDnsServer);

            var responseLengthBuffer = new byte[2];
            responseLengthBuffer[0] = (byte)(responseMessage.Length >> 8);
            responseLengthBuffer[1] = (byte)(responseMessage.Length & 0xFF);

            await sslStream.WriteAsync(responseLengthBuffer, 0, 2);
            await sslStream.WriteAsync(responseMessage, 0, responseMessage.Length);
        }
    }
}
