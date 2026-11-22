using System.Net;
using System.Net.Sockets;
using DnsQuery;

var tests = new (string Name, Func<Task> Run)[]
{
    ("DoT framing reads partial stream segments", TestPartialReadsAsync),
    ("DoT framing rejects partial EOF", TestPartialEofAsync),
    ("DoH base64url decoding owns valid memory", TestBase64UrlAsync),
    ("UDP responses match concurrent requests", TestConcurrentUdpAsync),
    ("Late UDP response cannot contaminate next query", TestLateUdpResponseAsync),
    ("IPv6 upstream works", TestIpv6Async),
    ("Truncated UDP response falls back to TCP", TestTcpFallbackAsync)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static async Task TestPartialReadsAsync()
{
    var source = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
    await using var stream = new ChunkedReadStream(source, 1);
    var destination = new byte[source.Length];
    Assert(await StreamProtocol.ReadExactlyOrEofAsync(stream, destination, CancellationToken.None));
    Assert(source.SequenceEqual(destination));
}

static async Task TestPartialEofAsync()
{
    await using var stream = new ChunkedReadStream([1, 2, 3], 2);
    try
    {
        await StreamProtocol.ReadExactlyOrEofAsync(stream, new byte[4], CancellationToken.None);
        throw new Exception("Expected EndOfStreamException.");
    }
    catch (EndOfStreamException)
    {
    }
}

static Task TestBase64UrlAsync()
{
    var query = Helpers.CreateDnsQuery("example.com");
    var encoded = Convert.ToBase64String(query).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    Assert(Helpers.TryDecodeBase64Url(encoded, ushort.MaxValue, out var decoded));
    GC.Collect();
    Assert(query.SequenceEqual(decoded));
    Assert(!Helpers.TryDecodeBase64Url("%%%", ushort.MaxValue, out _));
    return Task.CompletedTask;
}

static async Task TestConcurrentUdpAsync()
{
    await using var server = await FakeDnsServer.StartAsync(IPAddress.Loopback, reorderBatchSize: 64);
    await using var resolver = CreateResolver(server.EndPoint, TimeSpan.FromSeconds(2));
    var queries = Enumerable.Range(0, 64).Select(index =>
    {
        var query = Helpers.CreateDnsQuery($"n{index}.example");
        DnsMessage.WriteId(query, (ushort)(1000 + index));
        return query;
    }).ToArray();

    var responses = await Task.WhenAll(queries.Select(query =>
        resolver.ResolveAsync(query, CancellationToken.None).AsTask()));
    for (var i = 0; i < queries.Length; i++)
    {
        Assert(DnsMessage.ReadId(responses[i]) == DnsMessage.ReadId(queries[i]));
        Assert(queries[i].AsSpan(4).SequenceEqual(responses[i].AsSpan(4)));
    }
}

static async Task TestLateUdpResponseAsync()
{
    await using var server = await FakeDnsServer.StartAsync(
        IPAddress.Loopback,
        firstResponseDelay: TimeSpan.FromMilliseconds(150));
    await using var resolver = CreateResolver(server.EndPoint, TimeSpan.FromMilliseconds(40));
    var first = Helpers.CreateDnsQuery("first.example");
    await AssertThrowsAsync<TimeoutException>(() => resolver.ResolveAsync(first, CancellationToken.None).AsTask());
    await Task.Delay(180);

    var second = Helpers.CreateDnsQuery("second.example");
    var response = await resolver.ResolveAsync(second, CancellationToken.None);
    Assert(DnsMessage.ReadId(response) == DnsMessage.ReadId(second));
    Assert(second.AsSpan(4).SequenceEqual(response.AsSpan(4)));
}

static async Task TestIpv6Async()
{
    if (!Socket.OSSupportsIPv6)
        return;

    await using var server = await FakeDnsServer.StartAsync(IPAddress.IPv6Loopback);
    await using var resolver = CreateResolver(server.EndPoint, TimeSpan.FromSeconds(1));
    var query = Helpers.CreateDnsQuery("ipv6.example");
    var response = await resolver.ResolveAsync(query, CancellationToken.None);
    Assert(DnsMessage.ReadId(response) == DnsMessage.ReadId(query));
}

static async Task TestTcpFallbackAsync()
{
    await using var server = await FakeDnsServer.StartAsync(IPAddress.Loopback, truncateUdp: true);
    await using var resolver = CreateResolver(server.EndPoint, TimeSpan.FromSeconds(1));
    var query = Helpers.CreateDnsQuery("fallback.example");
    var response = await resolver.ResolveAsync(query, CancellationToken.None);
    Assert(!DnsMessage.IsTruncated(response));
    Assert(DnsMessage.ReadId(response) == DnsMessage.ReadId(query));
    Assert(server.TcpQueries == 1);
}

static UdpDnsResolver CreateResolver(IPEndPoint endpoint, TimeSpan timeout) => new(new DnsProxyOptions
{
    BaseDnsServer = endpoint.Address.ToString(),
    BaseDnsPort = endpoint.Port,
    CertificatePath = "unused-in-tests",
    UdpTimeout = timeout,
    TcpFallbackTimeout = TimeSpan.FromSeconds(1),
    MaxConcurrentUpstreamQueries = 128
});

static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    catch (T)
    {
    }
}

static void Assert(bool condition)
{
    if (!condition)
        throw new Exception("Assertion failed.");
}

sealed class ChunkedReadStream(byte[] content, int maximumChunk) : MemoryStream(content)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumChunk)], cancellationToken);
}

sealed class FakeDnsServer : IAsyncDisposable
{
    private readonly Socket _udp;
    private readonly TcpListener _tcp;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _udpLoop;
    private readonly Task _tcpLoop;
    private readonly int _reorderBatchSize;
    private readonly TimeSpan _firstResponseDelay;
    private readonly bool _truncateUdp;
    private int _udpQueries;
    private int _tcpQueries;

    private FakeDnsServer(
        Socket udp,
        TcpListener tcp,
        int reorderBatchSize,
        TimeSpan firstResponseDelay,
        bool truncateUdp)
    {
        _udp = udp;
        _tcp = tcp;
        _reorderBatchSize = reorderBatchSize;
        _firstResponseDelay = firstResponseDelay;
        _truncateUdp = truncateUdp;
        EndPoint = (IPEndPoint)udp.LocalEndPoint!;
        _udpLoop = UdpLoopAsync(_shutdown.Token);
        _tcpLoop = TcpLoopAsync(_shutdown.Token);
    }

    public IPEndPoint EndPoint { get; }
    public int TcpQueries => Volatile.Read(ref _tcpQueries);

    public static Task<FakeDnsServer> StartAsync(
        IPAddress address,
        int reorderBatchSize = 1,
        TimeSpan firstResponseDelay = default,
        bool truncateUdp = false)
    {
        var udp = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(address, 0));
        var endpoint = (IPEndPoint)udp.LocalEndPoint!;
        var tcp = new TcpListener(address, endpoint.Port);
        tcp.Start();
        return Task.FromResult(new FakeDnsServer(udp, tcp, reorderBatchSize, firstResponseDelay, truncateUdp));
    }

    private async Task UdpLoopAsync(CancellationToken cancellationToken)
    {
        var batch = new List<(byte[] Message, EndPoint Remote)>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var buffer = new byte[ushort.MaxValue];
                EndPoint any = EndPoint.AddressFamily == AddressFamily.InterNetwork
                    ? new IPEndPoint(IPAddress.Any, 0)
                    : new IPEndPoint(IPAddress.IPv6Any, 0);
                var received = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken);
                var message = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
                batch.Add((message, received.RemoteEndPoint));
                if (batch.Count < _reorderBatchSize)
                    continue;

                batch.Reverse();
                foreach (var item in batch)
                {
                    var number = Interlocked.Increment(ref _udpQueries);
                    if (number == 1 && _firstResponseDelay > TimeSpan.Zero)
                        await Task.Delay(_firstResponseDelay, cancellationToken);
                    item.Message[2] |= 0x80;
                    if (_truncateUdp)
                        item.Message[2] |= 0x02;
                    await _udp.SendToAsync(item.Message, SocketFlags.None, item.Remote, cancellationToken);
                }
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Windows reports ICMP port-unreachable on the next UDP receive when a
            // deliberately late test response targets a socket that has gone away.
        }
    }

    private async Task TcpLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await _tcp.AcceptTcpClientAsync(cancellationToken);
                await using var stream = client.GetStream();
                var prefix = new byte[2];
                await StreamProtocol.ReadExactlyOrEofAsync(stream, prefix, cancellationToken);
                var length = (prefix[0] << 8) | prefix[1];
                var message = new byte[length];
                await StreamProtocol.ReadExactlyOrEofAsync(stream, message, cancellationToken);
                Interlocked.Increment(ref _tcpQueries);
                message[2] = (byte)((message[2] | 0x80) & ~0x02);
                await stream.WriteAsync(prefix, cancellationToken);
                await stream.WriteAsync(message, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _udp.Dispose();
        _tcp.Stop();
        await Task.WhenAll(_udpLoop, _tcpLoop);
        _shutdown.Dispose();
    }
}
