using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using DnsQuery;

var builder = WebApplication.CreateBuilder(args);
var options = DnsProxyOptions.FromConfiguration(builder.Configuration);
options.Validate();

var certificate = X509CertificateLoader.LoadPkcs12FromFile(
    options.CertificatePath,
    options.CertificatePassword);

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = options.MaxDnsMessageSize;
    serverOptions.Limits.MaxConcurrentConnections = options.MaxDohConnections;
    serverOptions.Listen(IPAddress.IPv6Any, options.DohPort, listenOptions =>
        listenOptions.UseHttps(certificate));
});

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(certificate);
builder.Services.AddSingleton<IDnsResolver, UdpDnsResolver>();
builder.Services.AddHostedService<DnsOverTlsServer>();
builder.Services.AddRateLimiter(rateLimiterOptions =>
{
    rateLimiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetConcurrencyLimiter(
            "doh",
            _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = options.MaxConcurrentDohRequests,
                QueueLimit = options.MaxQueuedDohRequests,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));
});

var app = builder.Build();
app.UseRateLimiter();
app.MapDnsOverHttps();

await app.RunAsync();
