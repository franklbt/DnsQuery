namespace DnsQuery;

public static class StreamProtocol
{
    public static async ValueTask<bool> ReadExactlyOrEofAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], cancellationToken);
            if (read == 0)
            {
                if (offset == 0)
                    return false;

                throw new EndOfStreamException("The stream ended in the middle of a DNS frame.");
            }

            offset += read;
        }

        return true;
    }
}
