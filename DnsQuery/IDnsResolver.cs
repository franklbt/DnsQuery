namespace DnsQuery;

public interface IDnsResolver
{
    ValueTask<byte[]> ResolveAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken);
}
