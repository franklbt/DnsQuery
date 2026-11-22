using System.Net;

namespace DnsQuery;

public sealed class DnsProxyOptions
{
    public string BaseDnsServer { get; init; } = "127.0.0.1";
    public int BaseDnsPort { get; init; } = 53;
    public string CertificatePath { get; init; } = string.Empty;
    public string? CertificatePassword { get; init; }
    public int DotPort { get; init; } = 853;
    public int DohPort { get; init; } = 443;
    public int MaxDnsMessageSize { get; init; } = ushort.MaxValue;
    public int MaxConcurrentUpstreamQueries { get; init; } = 256;
    public int MaxTlsConnections { get; init; } = 256;
    public int MaxQueriesPerTlsConnection { get; init; } = 8;
    public int MaxDohConnections { get; init; } = 512;
    public int MaxConcurrentDohRequests { get; init; } = 256;
    public int MaxQueuedDohRequests { get; init; } = 256;
    public TimeSpan UdpTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan TcpFallbackTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan TlsHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan TlsIdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public IPEndPoint BaseDnsEndPoint => new(IPAddress.Parse(BaseDnsServer), BaseDnsPort);

    public static DnsProxyOptions FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration.GetSection("DnsProxy").Get<DnsProxyOptions>() ?? new DnsProxyOptions();

        // Keep the original flat settings working during upgrades.
        return new DnsProxyOptions
        {
            BaseDnsServer = configuration["DnsProxy:BaseDnsServer"]
                ?? configuration["BaseDnsServer"]
                ?? configured.BaseDnsServer,
            BaseDnsPort = configured.BaseDnsPort,
            CertificatePath = configuration["DnsProxy:CertificatePath"]
                ?? configuration["CertificatePath"]
                ?? configured.CertificatePath,
            CertificatePassword = configuration["DnsProxy:CertificatePassword"]
                ?? configuration["CertificatePassword"]
                ?? configured.CertificatePassword,
            DotPort = configured.DotPort,
            DohPort = configured.DohPort,
            MaxDnsMessageSize = configured.MaxDnsMessageSize,
            MaxConcurrentUpstreamQueries = configured.MaxConcurrentUpstreamQueries,
            MaxTlsConnections = configured.MaxTlsConnections,
            MaxQueriesPerTlsConnection = configured.MaxQueriesPerTlsConnection,
            MaxDohConnections = configured.MaxDohConnections,
            MaxConcurrentDohRequests = configured.MaxConcurrentDohRequests,
            MaxQueuedDohRequests = configured.MaxQueuedDohRequests,
            UdpTimeout = configured.UdpTimeout,
            TcpFallbackTimeout = configured.TcpFallbackTimeout,
            TlsHandshakeTimeout = configured.TlsHandshakeTimeout,
            TlsIdleTimeout = configured.TlsIdleTimeout
        };
    }

    public void Validate()
    {
        if (!IPAddress.TryParse(BaseDnsServer, out _))
            throw new InvalidOperationException("BaseDnsServer must be an IPv4 or IPv6 address.");
        if (BaseDnsPort is < 1 or > ushort.MaxValue)
            throw new InvalidOperationException("BaseDnsPort is invalid.");
        if (DotPort is < 1 or > ushort.MaxValue || DohPort is < 1 or > ushort.MaxValue)
            throw new InvalidOperationException("The DoT and DoH ports must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(CertificatePath))
            throw new InvalidOperationException("CertificatePath is required.");
        if (MaxDnsMessageSize is < DnsMessage.HeaderLength or > ushort.MaxValue)
            throw new InvalidOperationException("MaxDnsMessageSize must be between 12 and 65535.");
        if (MaxConcurrentUpstreamQueries <= 0 || MaxTlsConnections <= 0 ||
            MaxQueriesPerTlsConnection <= 0 || MaxDohConnections <= 0 ||
            MaxConcurrentDohRequests <= 0 || MaxQueuedDohRequests < 0)
            throw new InvalidOperationException("Concurrency limits must be positive.");
        if (UdpTimeout <= TimeSpan.Zero || TcpFallbackTimeout <= TimeSpan.Zero ||
            TlsHandshakeTimeout <= TimeSpan.Zero || TlsIdleTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Timeouts must be positive.");
    }
}
