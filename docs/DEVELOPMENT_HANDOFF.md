# Caddy UI 2.0 – Entwicklungsübergabe

Status: verbindliche zentrale Arbeitsgrundlage  
Aktueller Entwicklungszweig: `agent/dotnet-postgres-phase-9`  
Aktueller Draft-PR: #28  
Basis: Phase 8 / PR #27  
Produktiver Stand: Python/SQLite bleibt bis zur kontrollierten Phase-9-Umschaltung aktiv

Dieses Dokument ist die zentrale Fortsetzungsgrundlage. Detailregeln stehen in den Fachverträgen und `PHASE_*_STATUS.md`. Bei Widersprüchen gelten der spezifischere Vertrag und die zuletzt vollständig in CI verifizierte Implementierung.

## 1. Produktziel

Caddy UI wird schrittweise auf folgenden Stack umgebaut:

- .NET 10
- ASP.NET Core Razor Pages
- PostgreSQL mit EF Core/Npgsql
- wenig JavaScript, kein SPA-Framework
- Companion- und Bundle-Container
- vorhandener externer Reverse Proxy

Das Produkt verwaltet und beobachtet:

- Admin- und Access-Portal-Authentifizierung
- Domains und DNS-Provider
- Wildcard- und Einzelzertifikate
- Routen und Zugriffsschutz
- Requests, Pageviews, Sessions und Clients
- IP Intelligence, Bot- und Risikobewertung
- DNS, DDNS, Jobs und Healthchecks
- Benachrichtigungen, Backups und Diagnose
- Shadow-Betrieb, Legacy-Migration und kontrollierte Umschaltung

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
  -> Navigationen / Pageviews / Sessions / Page Loads
  -> Aggregate
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

Cutover
  -> paralleler Shadow-Betrieb
  -> Readiness-Gate
  -> Legacy-Dry-Run / Import / Verify
  -> Statistikvergleich
  -> Wartungsfenster
  -> Portumschaltung
  -> Abnahme oder Rückfall
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
src/CaddyUi.Infrastructure  PostgreSQL, Provider, Worker, Dateien und Cutover
src/CaddyUi.Web             Razor Pages, Auth-Middleware und CSS
src/CaddyUi.Migration       idempotenter SQLite-/Legacy-Import

tests/*                     Unit-, Web-, PostgreSQL- und Acceptance-Tests
caddyguard                  Caddy-Schutzmodul
caddynetcp                  Netcup-DNS-Modul
docs                        Verträge, Status und Runbooks
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
| 9 | `agent/dotnet-postgres-phase-9` | #28 | Shadow-Readiness und kontrollierte Umschaltung |

Die Phasen werden in Reihenfolge integriert oder kontrolliert auf eine bereits integrierte Basis umgestellt. Kein unkontrollierter Rebase darf Sicherheits-, Migrations- oder Vertragsänderungen verlieren.

## 5. Implementierter Stand

### Phase 1 – Grundlage

- .NET-10-Solution mit getrennten Projekten
- Razor Pages
- PostgreSQL-Verbindung und Healthchecks
- Companion- und Bundle-Container
- Restore-, Format-, Build-, Test- und Container-CI

### Phase 2 – Persistenz und Migration

- Schema `caddy_ui`
- EF-Core-Migrationen
- idempotenter SQLite-Import
- persistente Data-Protection-Schlüssel
- partitionierte Request-Tabelle
- Analytics-, Security-, Job- und Auditpersistenz
- konsistente SQLite-Backups, Inspect, Dry-Run, Import und Verify

### Phase 3 – Authentifizierung und Domains

- Rollen `admin`, `editor`, `viewer`
- getrennte Admin- und Portal-Sitzungen
- LAN-, Public- und Portal-Surface-Prüfung
- Passwort-Hashing und Legacy-Rehash
- TOTP und Recovery-Codes
- progressive Login-Sperren
- CSRF-, Origin- und Proxy-Prüfung
- mehrere Domains und Provider-Katalog
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
- RIPEstat-Anreicherung mit Cache und Backoff
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
- Modi `disabled`, `shadow`, `active`
- atomischer Dateiaustausch
- Validate, Reload, Verify und Rollback
- serialisierte Schreiboperationen
- Routenimport und -export

### Phase 8 – DNS und Betrieb

- verwaltete DNS-Records
- DDNS-Ziele für A und AAAA
- direkte Provider-APIs und Verbindungstests
- geplante Jobs und Jobläufe
- öffentliche und interne Healthchecks
- SMTP, Webhook, Discord und Telegram
- PostgreSQL-Backups
- redigierte Diagnoseexporte
- Netcup-Wildcard-/DNS-01-Renderer
- kompakte Razor Pages für alle Betriebsbereiche

### Phase 9 – Shadow und Umschaltung

- `Cutover:Enabled=false` als expliziter sicherer Standard
- Readiness-Gate für PostgreSQL, Migrationen, Benutzer, Domains und Routen
- SHA-256-Identifikation der read-only Legacy-SQLite-Datei
- Prüfung gemeinsamer Caddy-Logs
- Mindestdauer und Aktualität des Shadow-Laufs
- Blockierung vorzeitig aktiver Schreibmodi
- Prüfung eines aktuellen PostgreSQL-Backups
- Legacy-/PostgreSQL-Statistikvergleich über dasselbe UTC-Zeitfenster
- konfigurierbare Abweichungstoleranz
- unveränderliche Readiness- und Statistikmanifeste
- administratorgeschützter Cutover-Arbeitsbereich
- vollständiges Produktions-, Abnahme- und Rollback-Runbook

Phase 9 bereitet die Umschaltung vor, führt sie aber nicht automatisch aus.

## 6. Statistikvertrag

### Requests

Jeder serverseitig verarbeitete HTTP-Request zählt einmal. Dazu gehören HTML, JavaScript, CSS, Bilder, Fonts, APIs, WebSocket-Upgrades, Healthchecks, Redirects, Bots und Fehler.

### Pageviews

Ein Pageview entsteht nur bei:

- erfolgreicher menschlicher Dokumentnavigation mit `2xx` oder `304`
- bestätigtem SPA-Routenwechsel über optionales First-Party-Beacon

Redirects und Dokumentfehler bleiben Requests beziehungsweise Navigationen, aber keine erfolgreichen Pageviews.

### Beispiel Mealie/Nuxt

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

### Cutover-Vergleich

Legacy und .NET werden nur über dasselbe geschlossene UTC-Zeitfenster verglichen:

- Requests
- Pageviews
- Sessions
- Clients
- HTTP-5xx-Fehler

Standardtoleranz: höchstens 5 Prozent Abweichung je Kennzahl.

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

Ein aktiver Wildcard-Apply ist nur möglich, wenn Provider, Caddy-DNS-Modul, Renderer und Secret-Referenzen einsatzbereit sind. Der aktuelle Bundle-Renderer unterstützt Netcup.

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

Direkte Record-API und DDNS:

- Netcup
- Cloudflare
- DigitalOcean
- Hetzner DNS
- IONOS
- Gandi
- deSEC
- DuckDNS

Die UI unterscheidet Management-Katalog, direkten API-Adapter, installiertes Caddy-DNS-Modul und geprüften Wildcard-Renderer.

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
- Logs, Diffs, Manifeste, Cutover-Reports und UI zeigen keine aufgelösten Werte.

## 10. Betriebsmodi und sichere Defaults

```text
Analytics:Enabled=false
IpSecurity:IntelligenceEnabled=false
IpSecurity:RiskAssessmentEnabled=false
IpSecurity:BlockWriteMode=disabled
Routing:WriteMode=disabled
Operations:WorkerEnabled=false
Operations:DnsWriteMode=disabled
Cutover:Enabled=false
Database:ApplyMigrationsOnStartup=false
DataProtection:PersistKeysToPostgreSql=true
```

Bedeutung:

- `disabled`: kein produktiver Schreibzugriff
- `shadow`: validierter Vorschaupfad ohne produktive Wirkung
- `active`: explizit aktivierter produktiver Pfad

Python/SQLite bleibt bis zur kontrollierten Umschaltung der produktive Schreibpfad.

## 11. UI-Vertrag

Maßgeblich: `docs/UI_DESIGN_CONTRACT.md`.

- AE01-inspirierter Arbeitsstil
- neutraler Hintergrund statt Weiß-auf-Weiß
- klar abgegrenzte helle Arbeitsflächen
- sichtbare Rahmen und Fokuszustände
- 34-px-Standardcontrols und 30-px-Kompaktcontrols
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

- Records zuerst als PostgreSQL-Entwurf speichern
- Domain und Provider fest zuordnen
- Synchronisierung als separate sichtbare Aktion
- Status und Fehler speichern

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

## 13. Cutover-Vertrag

Maßgeblich: `docs/CUTOVER_RUNBOOK.md`.

Readiness blockiert bei:

- fehlender expliziter Freigabe
- nicht erreichbarem PostgreSQL oder ausstehenden Migrationen
- fehlender Legacy-SQLite-Datei
- deaktivierter, veralteter oder zu kurzer Shadow-Ingestion
- aktiven produktiven Schreibmodi vor der Abnahme
- fehlendem Administratorkonto oder fehlenden Domains
- fehlendem beziehungsweise veraltetem PostgreSQL-Backup
- fehlendem oder abweichendem Statistik-Snapshot
- nicht beschreibbarem Manifestverzeichnis

Portumschaltung:

```text
Admin UI      -> .NET :8098
Access Portal -> .NET :8099
```

Die Anwendung schaltet diese Ports nicht automatisch um. Das Wartungsfenster verwendet Inspect, Dry-Run, finalen Import, Verify, Caddy-Validate, Snapshot, Reload, Abnahme und dokumentierten Rückfall.

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
- Legacy-Import und Verify
- Companion- und Bundle-Smoke
- HTTP-Flächen
- integriertes Caddy-Modul
- Routing-Golden-Master
- DNS-/DDNS-Persistenztests
- Authentifizierung aller Managementseiten
- Cutover-Komparator und Snapshotvalidierung

Phase 9 wurde in folgenden Läufen verifiziert:

- `Verify` `30471183169`
- `Verify .NET rebuild` `30471183630`

## 15. Produktionsisolation und Rollback

Bis zum Wartungsfenster:

- Python/SQLite bleibt produktiv
- .NET läuft intern im Shadow-Betrieb
- keine automatische Portumschaltung
- keine automatische DNS-, Routen- oder Blocklist-Aktivierung
- keine ungeprüfte Migration produktiver Secrets

Rollback:

- .NET-Worker deaktivieren
- DNS, Routing und Blocklist auf `disabled`
- vorherigen Caddy-Snapshot wiederherstellen
- Upstreams wieder auf Python/SQLite stellen
- vorhandene SQLite-Datei unverändert behalten
- PostgreSQL und Manifeste für die Analyse erhalten

## 16. Offene Produktionsvalidierung

- längerer Shadow-Lauf mit echten Logs
- Statistikvergleich Python gegen .NET
- Logrotation und Neustart
- finaler Legacy-Dry-Run, Import und Verify
- Provider-Test je produktiver Domain
- Shadow-DNS und DDNS
- Netcup-Wildcard-Zertifikat auf Testdomain
- Caddy-Reload und Rückfall
- Health-Fehlerbenachrichtigung
- Backup und Test-Wiederherstellung
- Diagnoseexport auf Secret-Leaks
- Worker-Sperre bei mehreren Instanzen
- vollständiger LAN-/Public-/Portal-Security-Test
- kontrollierte Portumschaltung

## 17. Nächste Phase

### Phase 10 – Python entfernen

Erst nach mindestens zwei stabilen .NET-Releases:

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
5. keine Secrets in Logs, UI, Diffs, Diagnose oder Cutover-Manifeste aufnehmen
6. Migrationen ergänzen, nicht umschreiben
7. Unit-, PostgreSQL-, Web- und Containerpfade testen
8. Phasenstatus und dieses Dokument aktualisieren
9. CI vollständig grün abwarten
10. Phase vor Abschluss auf einen konsolidierten Commit reduzieren

Keine Phase gilt als abgeschlossen, solange Restore, Format, Build, Tests, Migration und beide Containerpfade nicht verifiziert sind.
