using System.Net;
using Microsoft.AspNetCore.Mvc;
using static DnsQuery.Helpers;

namespace DnsQuery;

public class DnsOverHttpsServer
{
    private readonly WebApplication _app;
    public readonly IConfiguration Configuration;
    public readonly IPEndPoint BaseDnsServer;
    public ILogger Logger { get; }

    public DnsOverHttpsServer()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.IPv6Any, 443, l =>
            l.UseHttps(
                builder.Configuration.GetValue<string>("CertificatePath")!,
                builder.Configuration.GetValue<string>("CertificatePassword"))));
        builder.Services.AddLogging();

        _app = builder.Build();
        Configuration = _app.Configuration;
        BaseDnsServer = new IPEndPoint(IPAddress.Parse(
            _app.Configuration.GetValue<string>("BaseDnsServer")!), 53);
        Logger = _app.Logger;

        _app.MapMethods("/dns-query", new[] { "GET", "POST" },
            async (HttpContext context, ILogger<DnsOverHttpsServer> logger) =>
            {
                Memory<byte> dnsRequest;

                if (HttpMethods.IsGet(context.Request.Method))
                {
                    if (!context.Request.Query.TryGetValue("dns", out var dnsParam))
                        return Results.BadRequest("Paramètre 'dns' manquant.");

                    try
                    {
                        dnsRequest = FromBase64Url(dnsParam[0]!);
                        if (dnsRequest.Length == 0)
                            return Results.BadRequest("Paramètre 'dns' vide.");
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

                    var length = context.Request.Headers.ContentLength;
                    if (length is null or 0)
                        return Results.BadRequest("Corps de la requête vide.");

                    using var ms = new MemoryStream((int)length);
                    await context.Request.Body.CopyToAsync(ms);
                    dnsRequest = ms.GetBuffer().AsMemory(0, (int)ms.Length);
                }
                else
                {
                    return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
                }

                Memory<byte> dnsResponse;

                try
                {
                    dnsResponse = await ResolveDnsAsync(dnsRequest, BaseDnsServer);
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
