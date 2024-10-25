using DnsQuery;

var dnsOverHttpsServer = new DnsOverHttpsServer();
var dnsOverTlsServer = new DnsOverTlsServer(dnsOverHttpsServer);

Task.WhenAll(
    dnsOverHttpsServer.StartAsync(),
    dnsOverTlsServer.StartAsync()
).Wait();
