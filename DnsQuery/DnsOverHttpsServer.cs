using System.Net;
using Microsoft.AspNetCore.Mvc;
using static DnsQuery.Helpers;

namespace DnsQuery;

public class DnsOverHttpsServer
{
    private readonly WebApplication _app;
    private readonly string _baseDnsServer;
    public readonly IConfiguration Configuration;

    public DnsOverHttpsServer()
    {
        var builder = WebApplication.CreateBuilder([]);
        Configuration = builder.Configuration;
        _baseDnsServer = Configuration.GetValue<string>("BaseDnsServer")!;
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.IPv6Any, 443, l =>
            l.UseHttps(
                builder.Configuration.GetValue<string>("CertificatePath")!,
                builder.Configuration.GetValue<string>("CertificatePassword"))));
        builder.Services.AddLogging();

        _app = builder.Build();

        _app.MapMethods("/dns-query", new[] { "GET", "POST" },
            async (HttpContext context, ILogger<DnsOverHttpsServer> logger) =>
            {
                byte[] dnsRequest;

                if (HttpMethods.IsGet(context.Request.Method))
                {
                    if (!context.Request.Query.ContainsKey("dns"))
                        return Results.BadRequest("Paramètre 'dns' manquant.");

                    var dnsParam = context.Request.Query["dns"].ToString()!;

                    try
                    {
                        dnsRequest = FromBase64Url(dnsParam);
                    }
                    catch (FormatException)
                    {
                        return Results.BadRequest("Paramètre 'dns' invalide.");
                    }
                }
                else if (HttpMethods.IsPost(context.Request.Method))
                {
                    if (context.Request.ContentType is { } contentType
                        && !contentType.Equals("application/dns-message", StringComparison.OrdinalIgnoreCase))
                        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

                    using var ms = new MemoryStream();
                    await context.Request.Body.CopyToAsync(ms);
                    if(ms.Length == 0)
                        return Results.BadRequest("Corps de la requête vide.");
                    
                    dnsRequest = ms.ToArray();
                }
                else
                {
                    return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
                }

                byte[] dnsResponse;

                try
                {
                    dnsResponse = await ResolveDnsAsync(dnsRequest, _baseDnsServer);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Erreur lors de la résolution DNS.");
                    return Results.StatusCode(StatusCodes.Status502BadGateway);
                }

                return Results.Bytes(dnsResponse, "application/dns-message");
            });
    }

    public async Task StartAsync() => await _app.RunAsync();
}
