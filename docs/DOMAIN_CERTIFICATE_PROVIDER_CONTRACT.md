# Domain-, Zertifikats- und Providervertrag

Status: verbindlich für Caddy UI 2.0  
Eingeführt: Phase 3

## Domainzentriertes Routing

- Caddy UI kann mehrere verwaltete Domains enthalten.
- Jede neue Route muss genau einer verwalteten Domain zugeordnet werden.
- Die Route speichert innerhalb dieser Domain nur ihre Subdomain beziehungsweise den Root-Marker `@`.
- Der vollständige Host wird aus Domain und Subdomain abgeleitet und nicht unabhängig davon gepflegt.
- Importierte Legacy-Routen dürfen während der Übergangszeit noch keine `domain_id` besitzen. Vor Freigabe des .NET-Schreibpfads in Phase 7 müssen sie einmalig einer Domain zugeordnet werden.
- Eine Domain kann genau einen DNS-Provider verwenden. Ein Provider kann mehreren Domains zugeordnet sein.

## Zertifikatsstandard

- Der Standard für jede neue Domain ist `wildcard`.
- Neue Routen verwenden `inherit` und übernehmen damit den Domainstandard.
- `individual` ist eine bewusste Ausnahme pro Route oder Domain.
- Ein Zertifikat `*.example.com` deckt `app.example.com`, aber weder `example.com` noch `deep.app.example.com` ab.
- Für tiefere Ebenen wird eine eigene verwaltete Domain wie `app.example.com` oder ein explizites Einzelzertifikat benötigt.
- Die Domain- und Routenkonfiguration darf nicht stillschweigend wieder auf individuelle Zertifikate zurückfallen.
- Der vorhandene Wildcard-Vertrag aus `main` bleibt maßgeblich und wird beim Caddy-Schreibpfad in Phase 7 als Golden Master übernommen.

## DNS-Provider

Der Managementkatalog enthält:

- Netcup
- Cloudflare
- Amazon Route 53
- DigitalOcean
- Hetzner DNS
- IONOS
- OVHcloud
- Porkbun
- Namecheap
- Gandi LiveDNS
- deSEC
- Google Cloud DNS
- Microsoft Azure DNS
- Vultr
- Akamai Connected Cloud / Linode
- GoDaddy
- DuckDNS
- RFC 2136 / eigener DNS-Server

Provider deklarieren ihre Fähigkeiten für Zonensuche, Recordverwaltung, DNS-01 und DDNS. Provider, die keine DNS-01-Challenge unterstützen, dürfen nicht für eine Wildcard-Domain freigegeben werden.

## Secret-Vertrag

- API-Token, Passwörter und private Schlüssel werden nicht in `config_json` gespeichert.
- `secret_references_json` enthält ausschließlich Namen von Umgebungsvariablen oder `secret://`-Referenzen.
- Die UI zeigt Secretwerte nach dem Speichern nie wieder an.
- Provider-API-Tests und spätere Jobs lösen Referenzen erst zur Laufzeit auf.
- Logs, Audit-Diffs und Diagnoseexporte dürfen keine aufgelösten Secretwerte enthalten.

## Phasengrenze

Phase 3 stellt Schema, Katalog und Management bereit. Die produktiven Provider-API-Clients, DNS-/DDNS-Jobs und der Caddy-DNS-Renderer werden in Phase 8 implementiert. Der bestehende Netcup-/Wildcard-Produktivpfad in Python bleibt bis zur kontrollierten Umschaltung aktiv.
