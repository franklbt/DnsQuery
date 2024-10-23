using static DnsQuery.Helpers;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:80");
var app = builder.Build();

app.MapMethods("/dns-query", new[] { "GET", "POST" }, async context =>
{
    byte[] dnsRequest;

    if (HttpMethods.IsGet(context.Request.Method))
    {
        if (!context.Request.Query.ContainsKey("dns"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Paramètre 'dns' manquant.");
            return;
        }

        var dnsParam = context.Request.Query["dns"].ToString()!;

        try
        {
            dnsRequest = FromBase64Url(dnsParam);
        }
        catch (FormatException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Paramètre 'dns' invalide.");
            return;
        }
    }
    else if (HttpMethods.IsPost(context.Request.Method))
    {
        if (context.Request.ContentType is { } contentType
            && contentType.Equals("application/dns-message", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            await context.Response.WriteAsync("Type de contenu non supporté.");
            return;
        }

        using var ms = new MemoryStream();
        await context.Request.Body.CopyToAsync(ms);
        dnsRequest = ms.ToArray();
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        return;
    }

    byte[] dnsResponse;

    try
    {
        var baseDnsServer = app.Configuration.GetValue<string>("BaseDns");
        dnsResponse = await ResolveDnsAsync(dnsRequest, baseDnsServer);
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsync("Échec de la résolution DNS : " + ex.Message);
        return;
    }

    context.Response.ContentType = "application/dns-message";
    await context.Response.Body.WriteAsync(dnsResponse, 0, dnsResponse.Length);
});

app.Run();
