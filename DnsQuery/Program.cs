using DnsQuery;

var dnsOverHttpsServer = new DnsOverHttpsServer();
var dnsOverTlsServer = new DnsOverTlsServer(dnsOverHttpsServer.Configuration);

Task.WhenAll(
    dnsOverHttpsServer.StartAsync(),
    dnsOverTlsServer.StartAsync()
).Wait();
