using System.Buffers;
using Microsoft.Net.Http.Headers;

namespace DnsQuery;

public static class DnsOverHttpsServer
{
    public static IEndpointRouteBuilder MapDnsOverHttps(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/dns-query", ResolveGetAsync);
        endpoints.MapPost("/dns-query", ResolvePostAsync);
        return endpoints;
    }

    private static async Task<IResult> ResolveGetAsync(
        HttpContext context,
        IDnsResolver resolver,
        DnsProxyOptions options,
        ILoggerFactory loggerFactory)
    {
        if (!context.Request.Query.TryGetValue("dns", out var values) || values.Count != 1)
            return Results.BadRequest("Exactly one 'dns' query parameter is required.");

        if (!Helpers.TryDecodeBase64Url(values[0]!, options.MaxDnsMessageSize, out var request) ||
            !DnsMessage.IsQuery(request))
            return Results.BadRequest("The 'dns' query parameter is not a valid DNS message.");

        return await ResolveAsync(context, resolver, request, loggerFactory.CreateLogger("DoH"));
    }

    private static async Task<IResult> ResolvePostAsync(
        HttpContext context,
        IDnsResolver resolver,
        DnsProxyOptions options,
        ILoggerFactory loggerFactory)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("application/dns-message", StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

        byte[] request;
        try
        {
            request = await ReadRequestBodyAsync(
                context.Request,
                options.MaxDnsMessageSize,
                context.RequestAborted);
        }
        catch (InvalidDataException)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!DnsMessage.IsQuery(request))
            return Results.BadRequest("The body is not a valid DNS query.");

        return await ResolveAsync(context, resolver, request, loggerFactory.CreateLogger("DoH"));
    }

    private static async Task<IResult> ResolveAsync(
        HttpContext context,
        IDnsResolver resolver,
        byte[] request,
        ILogger logger)
    {
        try
        {
            var response = await resolver.ResolveAsync(request, context.RequestAborted);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(response, "application/dns-message");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            logger.LogWarning(ex, "The upstream DNS query failed.");
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The DNS-over-HTTPS query failed.");
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    internal static async ValueTask<byte[]> ReadRequestBodyAsync(
        HttpRequest request,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > maximumLength)
            throw new InvalidDataException("DNS request body is too large.");

        var rented = ArrayPool<byte>.Shared.Rent(maximumLength + 1);
        try
        {
            var length = 0;
            while (length <= maximumLength)
            {
                var read = await request.Body.ReadAsync(
                    rented.AsMemory(length, maximumLength + 1 - length),
                    cancellationToken);
                if (read == 0)
                    break;
                length += read;
            }

            if (length > maximumLength)
                throw new InvalidDataException("DNS request body is too large.");

            return rented.AsSpan(0, length).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
