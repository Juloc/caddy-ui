# Caddy UI 2.0 – Entwicklungsübergabe

Status: verbindliche zentrale Arbeitsgrundlage  
Aktueller Entwicklungszweig: `agent/dotnet-postgres-phase-8`  
Aktueller Draft-PR: #27  
Produktiver Stand: Python/SQLite bleibt bis Phase 9 aktiv

Dieses Dokument enthält den aktuellen Gesamtstand für die Weiterentwicklung. Detailregeln stehen zusätzlich in den Fachverträgen und `PHASE_*_STATUS.md`-Dateien. Bei Widersprüchen gelten der spezifischere Fachvertrag und die zuletzt vollständig in CI verifizierte Implementierung.

## 1. Produktziel

Caddy UI wird schrittweise von Python/SQLite auf folgenden Stack umgebaut:

- .NET 10
- ASP.NET Core Razor Pages
- PostgreSQL mit EF Core/Npgsql
- wenig JavaScript, kein SPA-Framework
- ein Companion- und ein Bundle-Container
- vorhandener externer Reverse Proxy

Das Produkt verwaltet und beobachtet:

- Admin- und Access-Portal-Authentifizierung
- Domains und DNS-Provider
- Wildcard- und Einzelzertifikate
- Routen und Zugriffsschutz
- Request-, Pageview-, Session- und Clientstatistik
- IP Intelligence und Risikobewertung
- DNS, DDNS, Jobs und Healthchecks
- Benachrichtigungen, Backups und Diagnose

Nicht Teil des Produkts:

- Docker-Socket-Verwaltung
- App-Templates
- schweres SPA-Frontend
- dekorative Marketingoberfläche

## 2. Zielarchitektur

```text
Browser
  -> Caddy
      -> Admin UI :8098
      -> Access Portal :8099
      -> verwaltete Upstreams

Caddy JSON Logs
  -> Tailer
  -> Parser + Redaction
  -> Klassifikation
  -> PostgreSQL Batch Writer
  -> Pageviews / Sessions / Aggregate
  -> Razor-Pages-Analytics

Management
  -> PostgreSQL-Entwurf
  -> Preview + Diff
  -> unveränderliche Revision
  -> Validate
  -> Shadow oder Active Apply
  -> Reload + Verify + Rollback

Betrieb
  -> Provider-API
  -> DNS / DDNS
  -> Jobs / Healthchecks
  -> Benachrichtigungen
  -> Backup / Diagnose
```

Container:

- `dotnet-companion`: UI, Portal und Jobs ohne gebündelten Caddy
- `dotnet-bundle`: gleicher .NET-Teil plus Caddy mit integrierten Modulen
- kein Docker-Socket
- Admin- und Portal-Port nicht direkt öffentlich veröffentlichen

## 3. Repository-Struktur

```text
src/CaddyUi.Contracts       Transport- und Statusverträge
src/CaddyUi.Domain          Fachtypen und Invarianten
src/CaddyUi.Application     Klassifikation, Compiler und Regeln
src/CaddyUi.Infrastructure  PostgreSQL, Provider, Worker und Dateien
src/CaddyUi.Web             Razor Pages, Auth-Middleware und CSS
src/CaddyUi.Migration       idempotenter SQLite-/Legacy-Import

tests/*                     Unit-, Web-, PostgreSQL- und Acceptance-Tests
caddyguard                  Caddy-Schutzmodul
caddynetcp                  Netcup-DNS-Modul
docs                        Verträge und Status
```

## 4. Gestapelte Branch- und PR-Kette

| Phase | Branch | PR | Inhalt |
| --- | --- | --- | --- |
| 1 | `agent/dotnet-postgres-phase-1` | #20 | Solution, Razor, Docker, CI |
| 2 | `agent/dotnet-postgres-phase-2` | #21 | PostgreSQL und Legacy-Migration |
| 3 | `agent/dotnet-postgres-phase-3` | #22 | Auth, Domains und Provider-Katalog |
| 4 | `agent/dotnet-postgres-phase-4` | #23 | Log-Ingestion und Statistik |
| 5 | `agent/dotnet-postgres-phase-5` | #24 | IP Intelligence und Security |
| 6 | `agent/dotnet-postgres-phase-6` | #25 | Read-only Analytics-UI |
| 7 | `agent/dotnet-postgres-phase-7` | #26 | Routing und kontrollierter Apply |
| 8 | `agent/dotnet-postgres-phase-8` | #27 | DNS, DDNS und Systemfunktionen |

Die Phasen werden in Reihenfolge integriert oder kontrolliert auf eine bereits integrierte Basis umgestellt. Kein unkontrollierter Rebase darf Sicherheits-, Migrations- oder Vertragsänderungen verlieren.

## 5. Implementierter Stand

### Phase 1 – Grundlage

- .NET-10-Solution
- getrennte Projekte
- Razor Pages
- PostgreSQL-Verbindung
- Healthchecks
- Companion- und Bundle-Container
- Restore-, Format-, Build-, Test- und Container-CI

### Phase 2 – Persistenz und Migration

- Schema `caddy_ui`
- EF-Core-Migrationen
- idempotenter SQLite-Import
- persistente Data-Protection-Schlüssel
- partitionierte Request-Tabelle
- Analytics-, Security-, Job- und Auditpersistenz

### Phase 3 – Authentifizierung und Domains

- Rollen `admin`, `editor`, `viewer`
- getrennte Admin- und Portal-Sitzungen
- LAN-, Public- und Portal-Surface-Prüfung
- Passwort-Hashing und Legacy-Rehash
- TOTP und Recovery-Codes
- progressive Login-Sperren
- CSRF-, Origin- und Proxy-Prüfung
- mehrere Domains
- Provider-Katalog
- Domainstandard `wildcard`
- Routenstandard `inherit`

### Phase 4 – Analytics

- Caddy-JSON-Parser
- Redaction vor Speicherung
- rotierbarer File-Tailer mit Checkpoints
- idempotenter Batchimport
- pseudonyme Clientkennung
- Requests, Navigationen, Pageviews, Page Loads und Sessions
- stündliche, tägliche, monatliche und routenbezogene Aggregate
- Retention und Partitionspflege

### Phase 5 – IP Security

- IPv4-/IPv6-Normalisierung
- kein externer Lookup für private oder reservierte Adressen
- RIPEstat-Anreicherung
- Cache und Backoff
- deterministische Risikoengine
- Clientdetail und Evidenz
- manuelle Sperren
- atomische Blocklist mit Rollback
- Security-, History- und Auditdaten

### Phase 6 – Read-only UI

- Übersicht mit Nutzungs- und Lastkennzahlen
- URL-basierte Filter
- Traffic, Requests, Clients und Routenanalyse
- Bots, Fehler und Performance
- Live-Log per Server-Sent Events
- System- und Ingestionstatus

### Phase 7 – Routing

- Proxy-, Redirect-, statische und optionale Custom-Routen
- Domain-, Host-, Pfad-, Port- und Injektionsvalidierung
- Zugriffsgruppen und Portalzugänge
- deterministischer Caddyfile-Compiler
- unveränderliche Revisionen
- Preview und Zeilen-Diff
- `disabled`, `shadow`, `active`
- atomischer Dateiaustausch
- Validate, Reload, Verify und Rollback
- serialisierte Schreiboperationen
- Routenimport und -export

### Phase 8 – DNS und Betrieb

- verwaltete DNS-Records
- DDNS-Ziele für A und AAAA
- direkte Provider-APIs
- Provider-Verbindungstests
- geplante Jobs und Jobläufe
- öffentliche und interne Healthchecks
- SMTP, Webhook, Discord und Telegram
- PostgreSQL-Backups
- redigierte Diagnoseexporte
- Netcup-Wildcard-/DNS-01-Renderer
- kompakte Razor-Pages für alle Betriebsbereiche

## 6. Statistikvertrag

### Requests

Jeder serverseitig verarbeitete HTTP-Request zählt einmal. Dazu gehören HTML, JavaScript, CSS, Bilder, Fonts, APIs, WebSocket-Upgrades, Healthchecks, Redirects, Bots und Fehler.

### Pageviews

Ein Pageview entsteht nur bei:

- erfolgreicher menschlicher Dokumentnavigation mit `2xx` oder `304`
- bestätigtem SPA-Routenwechsel über optionales First-Party-Beacon

Redirects und Dokumentfehler bleiben Requests beziehungsweise Navigationen, aber keine erfolgreichen Pageviews.

### Beispiel Mealie/Nuxt

Ein Dokument mit 100 JavaScript-/Nuxt-Assets ergibt:

```text
101 Requests
1 Navigation
1 Pageview
1 Page Load
100 Asset-Requests
```

`human + asset` bleibt ein Asset-Request und ist kein Pageview.

### Clients

Ohne First-Party-Beacon ist ein Client ein pseudonymer, geschätzter technischer Identifier und keine sicher erkannte Person.

## 7. Domains und Zertifikate

- jede Route besitzt eine `domain_id`
- Host wird aus Domain und Subdomain abgeleitet
- mehrere Domains werden unabhängig verwaltet
- neue Domains verwenden standardmäßig `wildcard`
- neue Routen verwenden standardmäßig `inherit`
- kein stiller Fallback auf Einzelzertifikate

Effektiver Modus:

```text
route=individual -> Einzelzertifikat
route=wildcard   -> Wildcard / DNS-01
route=inherit    -> Domainstandard
```

Ein aktiver Wildcard-Apply ist nur möglich, wenn Provider, Caddy-DNS-Modul, Renderer und Secret-Referenzen einsatzbereit sind. Andernfalls bleiben Preview und Shadow möglich, Active wird blockiert.

Der aktuelle Bundle-Renderer unterstützt Netcup mit Umgebungsvariablen für API-Key und API-Passwort.

## 8. Provider-Unterstützung

Management-Katalog:

- Netcup
- Cloudflare
- Amazon Route 53
- DigitalOcean
- Hetzner DNS
- IONOS
- OVHcloud
- Porkbun
- Namecheap
- Gandi
- deSEC
- Google Cloud DNS
- Azure DNS
- Vultr
- Linode/Akamai
- GoDaddy
- DuckDNS
- RFC 2136

Direkte Record-API und DDNS in Phase 8:

- Netcup
- Cloudflare
- DigitalOcean
- Hetzner DNS
- IONOS
- Gandi
- deSEC
- DuckDNS

Die UI unterscheidet klar zwischen:

- Management-Katalog vorhanden
- direkter API-Adapter vorhanden
- Caddy-DNS-Modul installiert
- Wildcard-Renderer geprüft

## 9. Secret-Vertrag

Erlaubte Referenzen:

```text
ENV_NAME
secret://env/ENV_NAME
secret://file/absolute/path
```

- PostgreSQL enthält keine Secretwerte.
- Provider lösen Secrets erst beim Aufruf auf.
- Diagnoseexporte enthalten nur Secret-Feldnamen.
- Caddy-DNS-01 verwendet nur Umgebungsvariablen.
- Logs, Diffs, Manifeste und UI zeigen keine aufgelösten Werte.

## 10. Betriebsmodi und sichere Defaults

```text
Analytics:Enabled=false
IpSecurity:IntelligenceEnabled=false
IpSecurity:RiskAssessmentEnabled=false
IpSecurity:BlockWriteMode=disabled
Routing:WriteMode=disabled
Operations:WorkerEnabled=false
Operations:DnsWriteMode=disabled
Database:ApplyMigrationsOnStartup=false
DataProtection:PersistKeysToPostgreSql=true
```

Bedeutung:

- `disabled`: kein produktiver Schreibzugriff
- `shadow`: validierter Vorschau-/Shadowpfad ohne produktive Wirkung
- `active`: explizit aktivierter produktiver Pfad

Python/SQLite bleibt bis zur kontrollierten Umschaltung der produktive Schreibpfad.

## 11. UI-Vertrag

Maßgeblich: `docs/UI_DESIGN_CONTRACT.md`.

- AE01-inspirierter Arbeitsstil
- neutraler Hintergrund statt Weiß-auf-Weiß
- klar abgegrenzte helle Arbeitsflächen
- sichtbare Rahmen und Fokuszustände
- 34-px-Standardcontrols
- 30-px-Kompaktcontrols
- klare Primär-, Sekundär- und Gefahraktionen
- dichte Tabellen statt Card-Wänden
- flache Arbeitsbereiche
- keine Gradienten, Glows, Blur- oder Glasflächen
- keine unnötigen Animationen
- responsive und Reduced-Motion-kompatibel
- servergerendert und schnell

## 12. DNS, DDNS und Jobs

Maßgeblich: `docs/DNS_OPERATIONS_CONTRACT.md`.

DNS:

- Records werden zuerst als PostgreSQL-Entwurf gespeichert.
- Domain und Provider müssen einander fest zugeordnet sein.
- Synchronisierung ist eine separate sichtbare Aktion.
- Status und Fehler werden gespeichert.

DDNS:

- A und AAAA
- öffentliche oder statische Adresse
- mehrere öffentliche IP-Dienste als Fallback
- kein Write bei unveränderter Adresse
- exklusive Beanspruchung über `FOR UPDATE SKIP LOCKED`

Jobs:

- `ddns`
- `provider-test`
- `health`
- `backup`
- dauerhafte Run-Historie und Correlation-ID

## 13. Benachrichtigungen, Health und Backups

Benachrichtigungen:

- dauerhafte In-App-Meldung
- SMTP-E-Mail
- HTTPS-Webhook
- Discord
- Telegram

Health:

- öffentliche URLs
- interne Upstreams
- Statusbereich und Timeout pro Ziel
- Verlauf und Zustandswechsel

Backup:

- PostgreSQL-Custom-Dump
- redigierter Diagnoseexport
- vorhandene Caddy-Konfigurationsdateien
- Manifest und SHA-256-Digest
- begrenzte Aufbewahrung

## 14. CI und lokale Verifikation

```sh
dotnet restore CaddyUi.slnx
dotnet format CaddyUi.slnx --verify-no-changes
dotnet build CaddyUi.slnx --configuration Release --no-restore
dotnet test CaddyUi.slnx --configuration Release --no-build

go test ./...

docker build --file Dockerfile.dotnet --target dotnet-companion .
docker build --file Dockerfile.dotnet --target dotnet-bundle .
```

Zusätzlich erforderlich:

- PostgreSQL-Migration auf leerer Datenbank
- Legacy-Import
- Companion- und Bundle-Smoke
- HTTP-Flächen
- integriertes Caddy-Modul
- Routing-Golden-Master
- DNS-/DDNS-Persistenztests
- Authentifizierung aller Managementseiten

## 15. Produktionsisolation und Rollback

Bis Phase 9:

- Python/SQLite bleibt produktiv
- .NET läuft intern oder im Shadow-Betrieb
- keine automatische Portumschaltung
- keine automatische DNS-Aktivierung
- keine automatische Routenaktivierung
- keine automatische Blocklist-Aktivierung
- keine ungeprüfte Migration produktiver Secrets

Rollback:

- .NET-Worker deaktivieren
- DNS- und Routingmodus auf `disabled`
- Python-Container weiterverwenden
- vorhandene SQLite-Datei unverändert behalten
- Caddy-Snapshot wiederherstellen

## 16. Offene Produktionsvalidierung

- längerer Shadow-Lauf mit echten Logs
- Statistikvergleich Python gegen .NET
- Logrotation und Neustart
- Provider-Test je produktiver Domain
- Shadow-DNS und DDNS
- Netcup-Wildcard-Zertifikat auf Testdomain
- Caddy-Reload und Rollback
- Health-Fehlerbenachrichtigung
- Backup und Test-Wiederherstellung
- Diagnoseexport auf Secret-Leaks
- Worker-Sperre bei mehreren Instanzen
- vollständiger LAN-/Public-/Portal-Security-Test

## 17. Nächste Phasen

### Phase 9 – Shadow-Betrieb und Umschaltung

- paralleler interner Betrieb
- gleiche Logs read-only verarbeiten
- Statistikvergleich
- finaler Legacy-Dry-Run
- Wartungsfenster
- finaler SQLite-Import
- Umschaltung von 8098 und 8099
- Login-, Route-, DNS-, Zertifikat- und Statistikprüfung
- dokumentierter Rückfall

### Phase 10 – Python entfernen

Erst nach mindestens zwei stabilen Releases:

- Python-Laufzeit entfernen
- SQLite-Schreibpfade entfernen
- Legacy-Hotfixmodule entfernen
- Images verkleinern
- Dokumentation finalisieren
- Version `2.0.0` veröffentlichen

## 18. Fortsetzungscheckliste

1. richtigen gestapelten Head prüfen
2. dieses Dokument und betroffene Fachverträge lesen
3. vorhandene Implementierung und Tests prüfen
4. sichere Defaults beibehalten
5. keine Secrets in Logs, UI, Diffs oder Diagnose aufnehmen
6. Migrationen ergänzen, nicht umschreiben
7. Unit-, PostgreSQL-, Web- und Containerpfade testen
8. Phasenstatus und dieses Dokument aktualisieren
9. CI vollständig grün abwarten
10. Phase vor Abschluss auf einen konsolidierten Commit reduzieren

Keine Phase gilt als abgeschlossen, solange Restore, Format, Build, Tests, Migration und beide Containerpfade nicht verifiziert sind.
