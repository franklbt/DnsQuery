namespace DnsQuery;

using System;
using System.Collections.Concurrent;
using System.Net.Sockets;

public class UdpClientPool : IDisposable
{
    private readonly ConcurrentBag<UdpClient> _clients;
    private bool _disposed;

    public UdpClientPool(int initialCount = 15)
    {
        _clients = new ConcurrentBag<UdpClient>();
        for (var i = 0; i < initialCount; i++)
        {
            _clients.Add(new());
        }
    }

    public UdpClient Rent()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UdpClientPool));

        return _clients.TryTake(out var client) 
            ? client 
            : new();
    }

    public void Return(UdpClient client)
    {
        if (_disposed)
        {
            client.Dispose();
            return;
        }

        _clients.Add(client);
    }

    public void Dispose()
    {
        if (_disposed) 
            return;
        
        _disposed = true;
        
        while (_clients.TryTake(out var client)) 
            client.Dispose();
    }
}
