using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace DnsQuery;

public sealed class UdpDnsResolver : IDnsResolver, IAsyncDisposable
{
    private sealed record PendingQuery(
        ushort OriginalId,
        TaskCompletionSource<byte[]> Completion);

    private readonly DnsProxyOptions _options;
    private readonly Socket _socket;
    private readonly ConcurrentDictionary<ushort, PendingQuery> _pending = new();
    private readonly SemaphoreSlim _concurrency;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _receiveLoop;
    private Exception? _terminalError;
    private int _nextId;

    public UdpDnsResolver(DnsProxyOptions options)
    {
        _options = options;
        var endpoint = options.BaseDnsEndPoint;
        _socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.Connect(endpoint);
        _concurrency = new SemaphoreSlim(
            options.MaxConcurrentUpstreamQueries,
            options.MaxConcurrentUpstreamQueries);
        _receiveLoop = ReceiveLoopAsync(_shutdown.Token);
    }

    public async ValueTask<byte[]> ResolveAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        if (!DnsMessage.IsQuery(request.Span) || request.Length > _options.MaxDnsMessageSize)
            throw new InvalidDataException("Invalid DNS query.");
        if (Volatile.Read(ref _terminalError) is { } terminalError)
            throw new IOException("The upstream DNS receive loop is unavailable.", terminalError);

        await _concurrency.WaitAsync(cancellationToken);
        ushort upstreamId = 0;
        PendingQuery? pending = null;

        try
        {
            pending = RegisterPending(DnsMessage.ReadId(request.Span), out upstreamId);
            var rented = ArrayPool<byte>.Shared.Rent(request.Length);
            try
            {
                request.Span.CopyTo(rented);
                DnsMessage.WriteId(rented.AsSpan(0, 2), upstreamId);
                await _socket.SendAsync(
                    rented.AsMemory(0, request.Length),
                    SocketFlags.None,
                    cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            byte[] response;
            try
            {
                response = await pending.Completion.Task.WaitAsync(_options.UdpTimeout, cancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException("The upstream DNS server did not answer in time.", ex);
            }

            return DnsMessage.IsTruncated(response)
                ? await ResolveOverTcpAsync(request, cancellationToken)
                : response;
        }
        finally
        {
            if (pending is not null)
                _pending.TryRemove(upstreamId, out _);
            _concurrency.Release();
        }
    }

    private PendingQuery RegisterPending(ushort originalId, out ushort upstreamId)
    {
        for (var attempt = 0; attempt <= ushort.MaxValue; attempt++)
        {
            upstreamId = unchecked((ushort)Interlocked.Increment(ref _nextId));
            var pending = new PendingQuery(
                originalId,
                new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));
            if (_pending.TryAdd(upstreamId, pending))
                return pending;
        }

        throw new InvalidOperationException("No DNS transaction identifier is available.");
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(ushort.MaxValue);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int length;
                try
                {
                    length = await _socket.ReceiveAsync(rented, SocketFlags.None, cancellationToken);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is
                    SocketError.ConnectionReset or SocketError.ConnectionRefused)
                {
                    // An ICMP error belongs to one datagram. Its request will time out;
                    // the shared receive loop must remain available for later queries.
                    continue;
                }
                var message = rented.AsSpan(0, length);
                if (!DnsMessage.IsResponse(message))
                    continue;

                var upstreamId = DnsMessage.ReadId(message);
                if (!_pending.TryRemove(upstreamId, out var pending))
                    continue;

                var response = GC.AllocateUninitializedArray<byte>(length);
                message.CopyTo(response);
                DnsMessage.WriteId(response, pending.OriginalId);
                pending.Completion.TrySetResult(response);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _terminalError, ex);
            foreach (var pending in _pending.Values)
                pending.Completion.TrySetException(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async ValueTask<byte[]> ResolveOverTcpAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TcpFallbackTimeout);

        using var socket = new Socket(
            _options.BaseDnsEndPoint.AddressFamily,
            SocketType.Stream,
            ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(_options.BaseDnsEndPoint, timeout.Token);
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        var sendBuffer = ArrayPool<byte>.Shared.Rent(request.Length + 2);
        try
        {
            sendBuffer[0] = (byte)(request.Length >> 8);
            sendBuffer[1] = (byte)request.Length;
            request.Span.CopyTo(sendBuffer.AsSpan(2));
            await stream.WriteAsync(sendBuffer.AsMemory(0, request.Length + 2), timeout.Token);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sendBuffer);
        }

        var lengthPrefix = new byte[2];
        if (!await StreamProtocol.ReadExactlyOrEofAsync(stream, lengthPrefix, timeout.Token))
            throw new EndOfStreamException("The upstream DNS TCP connection closed before its response.");

        var length = (lengthPrefix[0] << 8) | lengthPrefix[1];
        if (length is < DnsMessage.HeaderLength || length > _options.MaxDnsMessageSize)
            throw new InvalidDataException("The upstream DNS TCP response has an invalid length.");

        var response = GC.AllocateUninitializedArray<byte>(length);
        await StreamProtocol.ReadExactlyOrEofAsync(stream, response, timeout.Token);
        if (!DnsMessage.IsResponse(response) || DnsMessage.ReadId(response) != DnsMessage.ReadId(request.Span))
            throw new InvalidDataException("The upstream DNS TCP response does not match the query.");

        return response;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _socket.Dispose();
        try
        {
            await _receiveLoop;
        }
        catch (Exception) when (_shutdown.IsCancellationRequested)
        {
        }

        foreach (var pending in _pending.Values)
            pending.Completion.TrySetCanceled(_shutdown.Token);
        _shutdown.Dispose();
        _concurrency.Dispose();
    }
}
