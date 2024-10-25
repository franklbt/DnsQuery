# DnsQuery

An highly optimized .NET service adding DNS over HTTP (DoS) and DNS over TLS (DoH) capabilities to Pi-Hole.

By letting RPi be accessible from the web, it can become a dns available for all devices while on the go (laptop, android phone...).

Need 443 and 853 ports to be free on Raspberry Pi (RPi).

## Dependencies

- .NET 9 SDK: [Download](https://dotnet.microsoft.com/fr-fr/download/dotnet/9.0)
- A RPi with Pi-Hole installed

## Build

Launch the following command:
```bash
dotnet publish -c Release
```
to build the project. The output will be located inside `/bin/Release/net9.0/linux-arm64`.

## Install on Raspberry PI

Before installing, the RPi must be accessible from the web, with a domain.
You can add a AAAA record from your dns provider pointing directly to the IPV6 address of the RPi.
Then [generate a certificate](https://eff-certbot.readthedocs.io/en/latest/using.html#renewing-certificates) with lets encrypt from the RPi for this domain. Save the path and the password of the certificate.

You can copy `linux-arm64` folder on your RPi and then launch theses commands inside the copied folder:

```bash
sudo setcap 'cap_net_bind_service=+ep' ./DnsQuery
chmod +x ./DnsQuery
```

After that, we need to edit `appsettings.json` file with the following values:

```json
{
  ...
  "BaseDnsServer": "<pihole_ip_address>",
  "CertificatePath": "<certificate_path>",
  "CertificatePassword": "<certificate_password>"
}
```

And then launch `./DnsQuery` inside the folder.

You can test that everything work by running this request (ask for google.com DNS record): 

```
https://<your_domain>/dns-query?dns=Wi8BAAABAAAAAAAABmdvb2dsZQNjb20AAAEAAQ 
```

You can automate the execution of this service by automating the launch via `systemd` for example.
