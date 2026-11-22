using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace DnsQuery;

public sealed class DnsOverTlsServer : BackgroundService
{
    private readonly IDnsResolver _resolver;
    private readonly DnsProxyOptions _options;
    private readonly X509Certificate2 _certificate;
    private readonly ILogger<DnsOverTlsServer> _logger;
    private readonly SemaphoreSlim _connectionSlots;
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private TcpListener? _listener;
    private long _connectionId;

    public DnsOverTlsServer(
        IDnsResolver resolver,
        DnsProxyOptions options,
        X509Certificate2 certificate,
        ILogger<DnsOverTlsServer> logger)
    {
        _resolver = resolver;
        _options = options;
        _certificate = certificate;
        _logger = logger;
        _connectionSlots = new SemaphoreSlim(options.MaxTlsConnections, options.MaxTlsConnections);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new TcpListener(IPAddress.IPv6Any, _options.DotPort);
        _listener.Server.DualMode = true;
        _listener.Start(backlog: Math.Min(_options.MaxTlsConnections, 512));
        _logger.LogInformation("DNS-over-TLS listening on TCP port {Port} (IPv4 and IPv6).", _options.DotPort);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _connectionSlots.WaitAsync(stoppingToken);
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch
                {
                    _connectionSlots.Release();
                    throw;
                }

                client.NoDelay = true;
                var id = Interlocked.Increment(ref _connectionId);
                var task = HandleClientAndReleaseAsync(client, stoppingToken);
                _connections[id] = task;
                _ = task.ContinueWith(
                    (completedTask, state) =>
                    {
                        _connections.TryRemove((long)state!, out var removedTask);
                    },
                    id,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Stop();
            await Task.WhenAll(_connections.Values);
        }
    }

    private async Task HandleClientAndReleaseAsync(TcpClient client, CancellationToken stoppingToken)
    {
        try
        {
            using (client)
            await using (var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
            {
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                handshakeTimeout.CancelAfter(_options.TlsHandshakeTimeout);
                await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                    },
                    handshakeTimeout.Token);

                await ProcessQueriesAsync(tls, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (AuthenticationException ex)
        {
            _logger.LogDebug(ex, "A DNS-over-TLS handshake failed.");
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "A DNS-over-TLS client disconnected.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A DNS-over-TLS connection failed.");
        }
        finally
        {
            _connectionSlots.Release();
        }
    }

    private async Task ProcessQueriesAsync(SslStream tls, CancellationToken stoppingToken)
    {
        var lengthPrefix = new byte[2];
        using var writeLock = new SemaphoreSlim(1, 1);
        var inFlight = new HashSet<Task>();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                while (inFlight.Count >= _options.MaxQueriesPerTlsConnection)
                {
                    await Task.WhenAny(inFlight);
                    inFlight.RemoveWhere(static task => task.IsCompleted);
                }

                using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                idleTimeout.CancelAfter(_options.TlsIdleTimeout);
                if (!await StreamProtocol.ReadExactlyOrEofAsync(tls, lengthPrefix, idleTimeout.Token))
                    break;

                var length = (lengthPrefix[0] << 8) | lengthPrefix[1];
                if (length is < DnsMessage.HeaderLength || length > _options.MaxDnsMessageSize)
                    throw new InvalidDataException("The DNS-over-TLS frame length is invalid.");

                IMemoryOwner<byte>? owner = MemoryPool<byte>.Shared.Rent(length);
                try
                {
                    var request = owner.Memory[..length];
                    await StreamProtocol.ReadExactlyOrEofAsync(tls, request, idleTimeout.Token);
                    if (!DnsMessage.IsQuery(request.Span))
                        throw new InvalidDataException("The DNS-over-TLS frame is not a query.");

                    var task = ProcessOneQueryAsync(owner, length, tls, writeLock, stoppingToken);
                    owner = null; // Ownership moved to the query task.
                    inFlight.Add(task);
                }
                finally
                {
                    owner?.Dispose();
                }

                inFlight.RemoveWhere(static task => task.IsCompleted);
            }
        }
        finally
        {
            await Task.WhenAll(inFlight);
        }
    }

    private async Task ProcessOneQueryAsync(
        IMemoryOwner<byte> requestOwner,
        int requestLength,
        SslStream tls,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        try
        {
            using (requestOwner)
            {
                var request = requestOwner.Memory[..requestLength];
                byte[] response;
                try
                {
                    response = await _resolver.ResolveAsync(request, cancellationToken);
                }
                catch (Exception ex) when (ex is TimeoutException or SocketException or IOException)
                {
                    _logger.LogWarning(ex, "The upstream DNS query failed; returning SERVFAIL.");
                    response = CreateServFail(request.Span);
                }

                var prefix = new byte[] { (byte)(response.Length >> 8), (byte)response.Length };
                await writeLock.WaitAsync(cancellationToken);
                try
                {
                    await tls.WriteAsync(prefix, cancellationToken);
                    await tls.WriteAsync(response, cancellationToken);
                }
                finally
                {
                    writeLock.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "A DNS-over-TLS response could not be written.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A DNS-over-TLS query failed.");
        }
    }

    private static byte[] CreateServFail(ReadOnlySpan<byte> query)
    {
        var response = query.ToArray();
        response[2] |= 0x80;
        response[3] = (byte)((response[3] & 0xf0) | 0x02);
        response.AsSpan(6, 4).Clear();
        return response;
    }

    public override void Dispose()
    {
        _listener?.Stop();
        _connectionSlots.Dispose();
        base.Dispose();
    }
}
