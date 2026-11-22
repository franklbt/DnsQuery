# DNSQuery

DNSQuery exposes a local Pi-hole resolver through DNS-over-TLS (DoT, TCP 853)
and DNS-over-HTTPS (DoH, HTTPS 443). Pi-hole remains the only upstream seen by
DNSQuery; configure Quad9 inside Pi-hole, not inside DNSQuery.

```text
phone/laptop -- DoT or DoH --> DNSQuery -- DNS UDP/TCP --> Pi-hole --> Quad9
```

The service targets .NET 10 LTS and Linux ARM64. It accepts IPv4 and IPv6
clients, and its Pi-hole endpoint can itself be IPv4 or IPv6.

## Configuration

`DnsQuery/appsettings.Example.json` lists every option. The original flat
`BaseDnsServer`, `CertificatePath`, and `CertificatePassword` keys remain
compatible. Prefer the `DnsProxy` section for new deployments.

Keep secrets outside Git. For systemd, put them in a root-owned file such as
`/etc/dnsquery/dnsquery.env`:

```ini
DnsProxy__BaseDnsServer=127.0.0.1
DnsProxy__CertificatePath=/etc/dnsquery/certificate.pfx
DnsProxy__CertificatePassword=replace-me
```

Then restrict it with `sudo chmod 600 /etc/dnsquery/dnsquery.env`. If Pi-hole
runs on another host, set its LAN IPv4 or IPv6 address instead of `127.0.0.1`.

### Point a domain at the Pi over IPv6

Register or renew your domain with your registrar (for example, Squarespace
Domains). The examples below use `example.net` and the hostname
`dns.example.net`. Substitute your own domain throughout.

1. On the Pi, run `ip -6 addr show scope global`. On the active Ethernet or
   Wi-Fi interface, choose a stable public IPv6 address (normally starting
   with 2 or 3). Omit the prefix length such as `/64` and skip addresses
   marked `temporary`.
2. In Squarespace, open the domain's **DNS Settings → Custom records**. Add
   type `AAAA`, host `dns`, and the Pi's public IPv6 address as the value.
   If another provider hosts your authoritative nameservers, edit the zone there.
3. In Windows PowerShell, run
   `Resolve-DnsName dns.example.net -Type AAAA -Server 9.9.9.9` and check the
   returned address. Allow inbound TCP 853 (DoT) and 443 (DoH) to the Pi in
   the router's IPv6 firewall and the Pi firewall. Clients need IPv6 connectivity
   to reach an IPv6-only service. Update the AAAA record if your ISP changes the prefix.

### Issue the certificate with a manual DNS-01 challenge

On Raspberry Pi OS, install the tools if needed and request the certificate:

```bash
sudo apt install certbot openssl
dns_host=dns.example.net
sudo certbot certonly --manual --preferred-challenges dns \
  --cert-name "$dns_host" -d "$dns_host"
```

Leave Certbot at its prompt. In the same DNS zone, add a **TXT** record with
host `_acme-challenge.dns` and paste the exact value Certbot displays.
From Windows, verify the published value:

```powershell
(Resolve-DnsName _acme-challenge.dns.example.net -Type TXT -Server 9.9.9.9).Strings
```

When the value matches, press Enter on the Pi. After successful issuance you
can remove that challenge value. DNS-01 works without opening port 80.

### Export the PFX and configure DNSQuery

Run the following in the same SSH session under the account used by the
systemd service. Use the certificate directory printed by Certbot if it
differs from the path below. OpenSSL prompts for an export password.

```bash
cert_dir="/etc/letsencrypt/live/$dns_host"
sudo install -d -o root -g "$(id -gn)" -m 0750 /etc/dnsquery
sudo openssl pkcs12 -export -in "$cert_dir/fullchain.pem" \
  -inkey "$cert_dir/privkey.pem" -out /etc/dnsquery/certificate.pfx
sudo chown "root:$(id -gn)" /etc/dnsquery/certificate.pfx
sudo chmod 0640 /etc/dnsquery/certificate.pfx
sudoedit /etc/dnsquery/dnsquery.env
```

Set `DnsProxy__CertificatePath` to `/etc/dnsquery/certificate.pfx` and
`DnsProxy__CertificatePassword` to the export password in that file, as shown
in **Configuration** above. These settings override `DnsProxy:CertificatePath`
and `DnsProxy:CertificatePassword` in the project's `appsettings.json`.
Keep `BaseDnsServer` pointing to Pi-hole. Apply the permissions above to the
environment file (mode 600). Once the service is installed, run:

```bash
sudo systemctl restart dnsquery
systemctl is-active dnsquery
```

For each manual renewal, repeat the Certbot command, publish the new TXT value,
export the updated PFX and restart DNSQuery. Unattended DNS-01 renewal requires
automation to update the challenge record. See the [Certbot manual plugin
guide](https://eff-certbot.readthedocs.io/en/stable/using.html#manual).

## Build, test, and benchmark

Install the .NET 10 SDK, then run:

```bash
dotnet build DnsQuery.sln -c Release -p:NuGetAudit=false
dotnet run --project DnsQuery.Tests/DnsQuery.Tests.csproj -c Release
dotnet run --project DnsQuery.Benchmarks/DnsQuery.Benchmarks.csproj -c Release -- 20000 128
```

The test project is a dependency-free executable so the protocol regression
suite can run without downloading a test framework. A non-zero exit code means
at least one test failed. The benchmark prints JSON and includes its runtime,
architecture, concurrency, throughput, allocations, working set, and GC counts.
Run the same command on the Raspberry Pi to obtain ARM64 measurements; desktop
numbers are not Raspberry Pi estimates.

On Windows, startup and idle-process measurements are reproducible with:

```powershell
.\scripts\Measure-Startup.ps1 -Runs 5
```

## Publish and install on Raspberry Pi

```bash
dotnet publish DnsQuery/DnsQuery.csproj -c Release -r linux-arm64 \
  --self-contained true -p:NuGetAudit=false -o artifacts/linux-arm64
```

### Transfer from Windows with scp

Enable SSH on the Pi and use PowerShell with the Windows OpenSSH client.
Run the commands below from the project root (the folder containing `DnsQuery.sln`).
Replace `alice@raspberrypi.local` with your SSH username and the Pi's hostname
or LAN address. These commands copy the ARM64 executable and systemd unit.
Configure the upstream and certificate on the Pi as described above.

```powershell
dotnet publish DnsQuery/DnsQuery.csproj -c Release -r linux-arm64 --self-contained true -o artifacts/linux-arm64
$piTarget = 'alice@raspberrypi.local'
ssh $piTarget 'mkdir -p ~/dnsquery-upload'
scp ./artifacts/linux-arm64/DnsQuery ./deploy/dnsquery.service "${piTarget}:dnsquery-upload/"
ssh $piTarget
```

Run these commands in the SSH session as your regular login user. The installation
sets the service user to that account and systemd uses its primary group.

```bash
sudo install -d /opt/dnsquery
sudo install -m 0755 ~/dnsquery-upload/DnsQuery /opt/dnsquery/DnsQuery
sed "s/^User=@SERVICE_USER@$/User=$(id -un)/" ~/dnsquery-upload/dnsquery.service | sudo tee /etc/systemd/system/dnsquery.service >/dev/null
sudo systemctl daemon-reload
sudo systemctl enable dnsquery
sudo systemctl restart dnsquery
systemctl show dnsquery -p User -p ActiveState
```

The unit grants only the capability required to bind ports 443 and 853; do not
run the service as root. Make the certificate readable by your SSH login user
without making its private key world-readable. Restart the service after a
certificate renewal because certificates are loaded at startup.

### View the service logs

On the Raspberry Pi, show the last 80 DNSQuery log entries from the current boot:

```bash
sudo journalctl -u dnsquery -b -n 80 --no-pager
```

If the service keeps restarting, look for the error immediately before
`Main process exited`. To follow new entries live:

```bash
sudo journalctl -u dnsquery -f
```

Press Ctrl+C to leave the live view.

## Quick DoH test

Replace `dns.example.net` with the hostname on your server certificate and open
this URL in a browser to request the IPv4 (A) record for `google.com`:

```text
https://dns.example.net/dns-query?dns=Wi8BAAABAAAAAAAABmdvb2dsZQNjb20AAAEAAQ
```

The response is a binary DNS message, so the browser may download a small file.
To check that Pi-hole received the query, look for `google.com` in its query log.

## Protocol behavior

- DoT reads the two-byte frame length and the DNS payload across any number of
  partial TCP/TLS reads, supports persistent connections and bounded pipelining,
  and applies handshake and idle timeouts.
- The upstream UDP dispatcher rewrites transaction IDs, correlates concurrent
  responses, ignores late responses, enforces an asynchronous timeout, and
  retries through DNS-over-TCP when Pi-hole sets the truncated bit.
- DoH implements GET and POST `/dns-query` with `application/dns-message`,
  bounded bodies, request cancellation, and concurrency limits.
- DoT and DoH listen dual-stack. The upstream socket uses the address family of
  the configured Pi-hole address.
