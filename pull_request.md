# DNSQuery V2 — traitement concurrent des requêtes DNS

Cette V2 revoit le chemin entre les clients DoT/DoH et Pi-hole pour traiter plusieurs requêtes simultanément avec un nombre borné de connexions et de requêtes en vol. Pi-hole reste responsable du filtrage et du choix du résolveur amont, par exemple Quad9.

## Changements à examiner

- **Proxy UDP.** Un socket connecté partagé remplace le pool de sockets. Chaque requête en vol reçoit un identifiant amont distinct. Le proxy restaure l’identifiant client avant de transmettre la réponse. Un délai asynchrone borne l’attente de la réponse UDP. Si Pi-hole signale une réponse tronquée, le proxy redemande la réponse au même serveur en TCP.
- **DNS over TLS.** Le serveur reconstitue le préfixe de longueur et le message même lorsqu’ils arrivent en plusieurs lectures. Une connexion TLS persistante peut porter plusieurs requêtes en vol, ce qui amortit le coût de la négociation TLS. Les limites de concurrence et les délais de négociation et d’inactivité sont configurables.
- **DNS over HTTPS.** Le décodage des requêtes GET conserve un tampon valide jusqu’à son utilisation. La lecture des corps POST est bornée, y compris en l’absence de Content-Length. La concurrence et la file d’attente HTTP sont plafonnées.
- **Déploiement.** Passage à .NET 10 LTS, prise en charge IPv4/IPv6 et publication autonome Linux ARM64. Les anciennes clés de configuration restent prises en charge. Le README couvre la configuration systemd, le certificat et la consultation des journaux.

## Performances mesurées

Résultats de l’audit sur Windows x64 avec .NET 10.0.15, après chauffe. Le benchmark UDP utilise un serveur simulé sur la boucle locale et traite 20 000 requêtes avec une concurrence maximale de 128.

| Indicateur | Mesure |
|---|---:|
| Débit du benchmark UDP | 21 423 requêtes/s |
| Allocations du processus, générateur compris | 1 384 octets/requête |
| Mémoire résidente du benchmark, avant puis après | 38,4 puis 46,9 Mo |
| Temps moyen jusqu’à l’ouverture du port DoT, sur cinq lancements distincts | 534 ms |

Ces chiffres constituent une référence V2. Le gain par rapport à la V1 et le débit DoT/DoH de bout en bout sur Raspberry Pi restent à mesurer.

## Validation et reproduction

La compilation Release a réussi. Les sept tests de régression ont passé sur Windows x64. Ils couvrent les lectures fragmentées, la fin de flux partielle, le tampon DoH, les réponses UDP réordonnées ou tardives, IPv6 et le repli TCP. La publication Linux ARM64 a également réussi.

Depuis la racine du dépôt, avec le SDK .NET 10 :

```sh
dotnet run --project DnsQuery.Tests/DnsQuery.Tests.csproj -c Release
dotnet run --project DnsQuery.Benchmarks/DnsQuery.Benchmarks.csproj -c Release -- 20000 128
```

Le protocole de mesure et les limites figurent dans [le rapport d’audit](AUDIT.md).
