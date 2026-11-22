using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using DnsQuery;

var queryCount = args.Length > 0 ? int.Parse(args[0]) : 20_000;
var concurrency = args.Length > 1 ? int.Parse(args[1]) : 128;
await using var upstream = new EchoDnsServer();
await using var resolver = new UdpDnsResolver(new DnsProxyOptions
{
    BaseDnsServer = IPAddress.Loopback.ToString(),
    BaseDnsPort = upstream.Port,
    CertificatePath = "unused-in-benchmark",
    UdpTimeout = TimeSpan.FromSeconds(5),
    MaxConcurrentUpstreamQueries = concurrency
});

await RunBatchAsync(Math.Min(1_000, queryCount), concurrency, resolver);
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

using var process = Process.GetCurrentProcess();
process.Refresh();
var workingSetBefore = process.WorkingSet64;
var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
var collectionsBefore = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray();
var stopwatch = Stopwatch.StartNew();

await RunBatchAsync(queryCount, concurrency, resolver);

stopwatch.Stop();
var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
process.Refresh();
var result = new
{
    measured = true,
    runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    workstationGc = !System.Runtime.GCSettings.IsServerGC,
    queryCount,
    concurrency,
    elapsedSeconds = stopwatch.Elapsed.TotalSeconds,
    queriesPerSecond = queryCount / stopwatch.Elapsed.TotalSeconds,
    allocatedBytes = allocated,
    allocatedBytesPerQuery = (double)allocated / queryCount,
    workingSetBeforeBytes = workingSetBefore,
    workingSetAfterBytes = process.WorkingSet64,
    gen0Collections = GC.CollectionCount(0) - collectionsBefore[0],
    gen1Collections = GC.CollectionCount(1) - collectionsBefore[1],
    gen2Collections = GC.CollectionCount(2) - collectionsBefore[2]
};
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

static Task RunBatchAsync(int count, int concurrency, UdpDnsResolver resolver) =>
    Parallel.ForEachAsync(
        Enumerable.Range(0, count),
        new ParallelOptions { MaxDegreeOfParallelism = concurrency },
        async (index, cancellationToken) =>
        {
            var query = Helpers.CreateDnsQuery("benchmark.example");
            DnsMessage.WriteId(query, (ushort)index);
            var response = await resolver.ResolveAsync(query, cancellationToken);
            if (DnsMessage.ReadId(response) != (ushort)index)
                throw new InvalidDataException("Mismatched DNS response.");
        });

sealed class EchoDnsServer : IAsyncDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;

    public EchoDnsServer()
    {
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _loop = RunAsync(_shutdown.Token);
    }

    public int Port { get; }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ushort.MaxValue];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await _socket.ReceiveFromAsync(
                    buffer,
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0),
                    cancellationToken);
                buffer[2] |= 0x80;
                await _socket.SendToAsync(
                    buffer.AsMemory(0, received.ReceivedBytes),
                    SocketFlags.None,
                    received.RemoteEndPoint,
                    cancellationToken);
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
        _socket.Dispose();
        await _loop;
        _shutdown.Dispose();
    }
}
