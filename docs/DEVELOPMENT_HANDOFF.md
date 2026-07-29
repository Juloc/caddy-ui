# Caddy UI 2.0 – Entwicklungsübergabe

Status: verbindliche zentrale Arbeitsgrundlage  
Aktueller Entwicklungszweig: `agent/dotnet-postgres-phase-7`  
Produktiver Stand: Python/SQLite bleibt bis zur kontrollierten Umschaltung aktiv

Dieses Dokument bündelt die Informationen für die Weiterentwicklung des .NET-/PostgreSQL-Neubaus. Detailverträge und `PHASE_*_STATUS.md`-Dateien ergänzen diese Übersicht. Bei Widersprüchen gelten die Sicherheits-, Domain-, Statistik-, UI- und Routingverträge sowie die zuletzt in CI verifizierte Implementierung.

## 1. Produktziel

Caddy UI ist eine kompakte, servergerenderte Verwaltungs- und Beobachtungsoberfläche für Caddy. Der Neubau ersetzt die Python-/SQLite-Anwendung schrittweise durch .NET 10, Razor Pages und PostgreSQL, ohne den produktiven Caddy-Betrieb während der Entwicklung zu gefährden.

Zielbereiche:

- Admin- und Access-Portal-Authentifizierung
- Domains, DNS-Provider und Zertifikate
- echte Request-, Pageview-, Session- und Clientstatistik
- IP Intelligence, Bot- und Risikobewertung
- Routen, Zugriffsschutz und kontrollierter Caddy-Schreibpfad
- DNS, DDNS, Benachrichtigungen, Backups und Diagnose
- kontrollierter Shadow-Betrieb, Migration und Umschaltung

Nicht Teil des Produkts:

- Docker-Socket-Verwaltung
- App-Templates
- schweres SPA-Framework
- dekorative Marketingoberfläche

## 2. Zielarchitektur

```text
Browser
  -> Caddy
      -> Admin UI auf 8098
      -> internes Access Portal auf 8099
      -> verwaltete Upstreams

Caddy JSON Access Logs
  -> Dateitailer
  -> Parser und Redaction
  -> Request-Klassifikation
  -> PostgreSQL Batch Writer
  -> Navigation, Pageview, Page Load und Session
  -> Aggregate und Read-only UI

Routing-Verwaltung
  -> PostgreSQL-Entwurf
  -> deterministischer Caddyfile-Compiler
  -> unveränderliche Revision
  -> Preview und Diff
  -> Validate
  -> Shadow oder atomischer Apply
  -> Reload, Verify und Rollback

Caddy UI .NET
  -> Razor Pages
  -> Application/Domain-Verträge
  -> Infrastructure Stores und Worker
  -> PostgreSQL
```

Containergrenze:

- `dotnet-companion`: UI, Portal und Jobs ohne gebündelten Caddy
- `dotnet-bundle`: gleiches UI-Image plus eigener Caddy-Build
- kein Docker-Socket
- Caddy-Admin-Port und Portal-Port dürfen nicht öffentlich veröffentlicht werden

## 3. Repository-Struktur

```text
src/CaddyUi.Contracts       Transport- und Statusverträge
src/CaddyUi.Domain          Fachtypen und Invarianten
src/CaddyUi.Application     Klassifikation, Compiler und Regeln
src/CaddyUi.Infrastructure  PostgreSQL, Provider, Worker und Dateischreibpfade
src/CaddyUi.Web             Razor Pages, Auth-Middleware und Assets
src/CaddyUi.Migration       idempotenter SQLite-/Legacy-Import

tests/*                     Unit-, Web-, Infrastruktur-, Migrations- und Acceptance-Tests
caddyguard                  integriertes Caddy-Schutzmodul
caddynetcp                  Netcup-DNS-Modul
docs                        Verträge, Status und diese Übergabe
```

## 4. Branch- und PR-Kette

Die Phasen sind gestapelt. Eine spätere Phase basiert auf dem Branch der vorherigen Phase.

| Phase | Branch | PR | Inhalt |
| --- | --- | --- | --- |
| 1 | `agent/dotnet-postgres-phase-1` | #20 | Solution, Razor-Grundlage, Docker und CI |
| 2 | `agent/dotnet-postgres-phase-2` | #21 | PostgreSQL-Schema und Legacy-Migration |
| 3 | `agent/dotnet-postgres-phase-3` | #22 | Authentifizierung, Domains und Provider |
| 4 | `agent/dotnet-postgres-phase-4` | #23 | Log-Ingestion und echte Statistik |
| 5 | `agent/dotnet-postgres-phase-5` | #24 | IP Intelligence, Risiko und Blockierung |
| 6 | `agent/dotnet-postgres-phase-6` | #25 | Read-only Statistikoberfläche |
| 7 | `agent/dotnet-postgres-phase-7` | nächster Draft-PR | Routen, Zugriff und kontrollierter Apply |

Vor einem Merge auf `main` wird die Kette der Reihe nach integriert oder kontrolliert auf die bereits integrierte Basis umgestellt. Kein unkontrollierter Rebase darf Sicherheits- oder Fachänderungen verlieren.

## 5. Implementierter Stand

### Phase 1 – Grundlage

- .NET-10-Solution mit getrennten Projekten
- Razor Pages und PostgreSQL-Verbindung
- Companion- und Bundle-Container
- Healthchecks
- CI für Restore, Format, Build, Tests und Container-Smoke

### Phase 2 – Persistenz und Migration

- PostgreSQL-Schema `caddy_ui`
- EF-Core-Migrationen
- idempotenter SQLite-/Legacy-Import
- Data-Protection-Schlüssel in PostgreSQL
- Kern-, Analytics-, Security- und Jobtabellen
- partitionierte `request_events`

### Phase 3 – Authentifizierung und Domains

- Rollen `admin`, `editor`, `viewer`
- getrennte Admin- und Portal-Sitzungen
- LAN-, Public- und Portal-Surface-Prüfung
- Passwort-Hashing, Legacy-scrypt-Rehash, TOTP und Recovery-Codes
- progressive Login-Sperren
- Origin-/Referer-/CSRF-Prüfung
- Domains, Provider-Katalog und Wildcard-Standard
- `CADDY_UI_REQUIRE_TOTP=false` bleibt erlaubt und sichtbar gewarnt

### Phase 4 – Analytics-Ingestion

- Caddy-JSON-Parser
- Secret-, Header- und Query-Redaction vor Speicherung
- File-Tailer mit Byte-Checkpoint und Rotationserkennung
- transaktionaler, idempotenter Import
- pseudonyme Clientkennung mit geschütztem HMAC-Schlüssel
- Requests, Navigationen, Pageviews, Page Loads und Sessions
- Stunden-, Tages-, Monats- und Routenaggregate
- Retention und Partitionswartung
- standardmäßig deaktivierter Shadow-Pfad

### Phase 5 – IP Security

- IPv4-/IPv6-Normalisierung und lokale Scope-Erkennung
- kein externer Lookup für private oder reservierte Netze
- RIPEstat Network Info und AS Overview
- Cache und exponentielles Fehler-Backoff
- versionierte deterministische Risikoengine `risk-v1`
- Reasons und Evidence je Bewertung
- Clientliste und Detailseite
- manuelles Blockieren und Entsperren
- atomische Blocklist mit Verifikation und Rollback
- Security-, History- und Audit-Einträge
- Betriebsmodi `disabled`, `shadow`, `active`

### Phase 6 – Read-only UI

- Dashboard mit Nutzungs- und Lastkennzahlen
- URL-basierte Filter für Zeitraum, Host, Akteur, Requesttyp und Status
- Traffic, Clients, Requests und Routenanalyse
- Bots & Sicherheit sowie Fehler & Performance
- Live-Log über Server-Sent Events
- System- und Ingestionstatus
- keine schreibende Caddy-Aktion aus Analytics-Seiten

### Phase 7 – Routing und kontrollierter Apply

- typisierte Routen für Proxy, Redirect, statische Antwort und optional Custom
- Domain-/Subdomain-/Host-, Pfad-, Port- und Injektionsvalidierung
- Routenliste und kompakter Editor
- Zugriffsgruppen und gehashte Portalzugänge
- deterministischer Caddyfile-Compiler
- unveränderliche Revisionen, Manifest und SHA-256-Digest
- Preview und Zeilen-Diff
- Schreibmodi `disabled`, `shadow`, `active`
- Kandidatenvalidierung
- atomischer Dateiaustausch
- vollständige Validierung, Reload und Nachprüfung
- automatische und manuelle Rollbacks
- serialisierte Apply-/Rollback-Operationen
- Snapshots, Operationsschritte und Auditdaten
- produktive Sperre für Wildcard-/Inherit-Zertifikate bis Phase 8
- AE01-inspirierter Fluent-Arbeitsstil für die gesamte Oberfläche

## 6. Statistikvertrag

### Request

Jeder verarbeitete HTTP-Request zählt genau einmal: HTML, JavaScript, CSS, Bilder, Fonts, APIs, WebSocket-Upgrades, Healthchecks, Redirects, Bots und Fehler. Requests beschreiben technische Serverlast.

### Navigation

Eine Navigation ist ein mutmaßlicher Dokumentaufruf. Evidenz:

1. First-Party-Beacon
2. `Sec-Fetch-Dest: document`
3. HTML-Accept-/Content-Type-Merkmale
4. `GET` oder `HEAD`
5. kein Asset-, API-, Healthcheck- oder interner Pfad
6. kein Bot-/Internal-Akteur

### Pageview

Ein Pageview entsteht bei erfolgreicher menschlicher Dokumentnavigation mit `2xx` oder `304`, beziehungsweise einem bestätigten SPA-Routenwechsel. Redirects bleiben Requests und Navigationen, aber keine eigenen Pageviews. Dokumentfehler sind fehlgeschlagene Navigationen.

### Page Load und Session

Ein Page Load gruppiert Pageview, Assets und APIs über Client, Host, Referer, Ziel und Zeitfenster. Eine Session endet standardmäßig nach 30 Minuten Inaktivität. Bots und interne Requests erzeugen keine normalen Besuchersessions.

### Client und Visitor

- `Client`: pseudonymer, ohne Beacon geschätzter technischer Identifier
- `Visitor`: optionaler First-Party-Identifier

Geschätzte Clients werden nie als sicher identifizierte Personen oder exakte Unique Visitors dargestellt.

### Mealie-/Nuxt-Beispiel

Ein Dokument plus 100 JavaScript-/Nuxt-Assets ergibt:

- 101 Requests
- 1 Navigation
- 1 Pageview
- 1 Page Load
- 100 Asset-Requests

`human + asset` ist ein technischer Asset-Request und niemals ein Pageview. Gehashte Frameworkpfade werden normalisiert.

## 7. UI-Vertrag

Maßgeblich ist `docs/UI_DESIGN_CONTRACT.md`.

Kernregeln:

- AE01-inspirierter Fluent-/Windows-11-Arbeitsstil
- kompakt, ruhig, schnell und aufgabenorientiert
- 34 px Standardcontrols, 30 px kompakte Controls
- 4/8/12/16/20/24/32-px-Abstandssystem
- klare Rahmen, Hover-, Auswahl- und Fokuszustände
- eine dominante Primäraktion pro Bereich
- klar erkennbare Sekundär- und Gefahraktionen
- neutrale Hintergrundfläche und sichtbar getrennte Arbeitsflächen
- flache Arbeitsbereiche statt verschachtelter Kartenwände
- dichte Tabellen statt einer Card je Zeile
- keine Gradienten, Glows, Blur-/Glasflächen oder schwere Animationen
- Segoe UI und Cascadia Code
- responsive und `prefers-reduced-motion`
- globaler Statusbereich ohne Layoutsprünge

## 8. Routing- und Apply-Vertrag

Maßgeblich ist `docs/ROUTING_APPLY_CONTRACT.md`.

### Route

Jede neue Route besitzt genau eine verwaltete Domain. Der vollständige Host wird aus Domain und Subdomain abgeleitet. Aktivierung bedeutet nur Aufnahme in die nächste Revision.

### Revision

Jede Revision ist unveränderlich und enthält vollständigen generierten Inhalt, Manifest, Digest, Grund, Ersteller und Zeitpunkt.

### Apply

```text
Generate
  -> Validate candidate
  -> Atomic write
  -> Validate complete config
  -> Reload Caddy
  -> Verify
  -> Audit
```

Bei Fehlern wird der vorherige Stand automatisch wiederhergestellt. Gleichzeitige Apply-/Rollback-Operationen werden verhindert.

### Zertifikate

- Domainstandard ist Wildcard.
- Routen verwenden standardmäßig `inherit`.
- kein stiller Rückfall auf Einzelzertifikate.
- Phase 7 erlaubt Shadow für Wildcard/Inherited.
- Active wird für Wildcard/Inherited bis zum Phase-8-Renderer blockiert.

### Legacy

Legacy-Routen ohne `domain_id` müssen vor Active-Freigabe einer Domain zugeordnet und im Shadow-Vergleich geprüft werden.

## 9. Sicherheitsvertrag

- Public Admin und Portal sind getrennte Oberflächen.
- Admin- und Portal-Cookies/Sitzungen werden nicht vermischt.
- interne Identitätsheader werden entfernt oder kontrolliert überschrieben.
- Secrets erscheinen nicht in Datenbank-Rohlogs, Preview, Diff, Diagnose oder UI.
- private/reservierte IPs gehen nicht an RIPEstat.
- Risikobewertung ist ein Hinweis, keine Identifikation.
- automatische produktive IP-Sperren bleiben standardmäßig deaktiviert.
- Dateiänderungen sind atomar, verifiziert und rückrollbar.
- jede Sperr-, Routen- und Apply-Aktion braucht Audit und Correlation-ID.
- Portalpasswörter werden gehasht und nie erneut angezeigt.
- Custom Routes bleiben standardmäßig deaktiviert.

## 10. PostgreSQL und Migrationen

Wichtige Tabellen:

- `users`, `admin_sessions`, `portal_sessions`, `login_attempts`, `login_blocks`
- `managed_domains`, `dns_providers`, `managed_routes`, `route_revisions`
- `access_groups`, `access_credentials`
- `caddy_snapshots`, `apply_operations`, `apply_operation_steps`
- `anonymous_clients`, `analytics_sessions`
- partitionierte `request_events`
- `navigation_events`, `page_views`, `page_loads`
- Traffic- und Performanceaggregate
- `analytics_checkpoints`, `ingestion_failures`
- `ip_intelligence_cache`, `client_assessments`, `client_assessment_reasons`
- `security_events`, `ip_block_rules`, `ip_block_history`, `audit_events`
- `scheduled_jobs`, `job_runs`

Regeln:

- jede Schemaänderung erhält eine neue Migration
- veröffentlichte Migrationen werden nicht umgeschrieben
- Migrationen werden gegen leere PostgreSQL-Datenbank und Legacy-Import getestet
- Raw Requests bleiben partitioniert und retentionfähig
- Import und Aggregate bleiben idempotent
- keine unbeschränkten In-Memory-Warteschlangen

## 11. Konfiguration und sichere Defaults

### Analytics

```text
Analytics:Enabled=false
Analytics:BatchSize=1000
Analytics:SessionIdleMinutes=30
Analytics:PageLoadWindowSeconds=15
Analytics:RawRequestRetentionDays=30
Analytics:PageViewRetentionDays=180
```

### IP Security

```text
IpSecurity:IntelligenceEnabled=false
IpSecurity:RiskAssessmentEnabled=false
IpSecurity:BlockWriteMode=disabled
IpSecurity:MaximumBlockHours=720
```

### Routing

```text
Routing:WriteMode=disabled
Routing:ManagedFragmentPath=/data/caddy-ui/generated/managed-routes.caddy
Routing:ShadowFragmentPath=/data/caddy-ui/shadow/managed-routes.caddy
Routing:RootConfigPath=/etc/caddy/Caddyfile
Routing:CaddyBinaryPath=/usr/bin/caddy
Routing:PortalUpstream=127.0.0.1:8099
Routing:CommandTimeoutSeconds=30
Routing:AllowCustomRoutes=false
```

Umgebungsvariablen:

```text
CADDY_UI_ROUTE_WRITE_MODE
CADDY_UI_MANAGED_ROUTES_PATH
CADDY_UI_ROUTE_SHADOW_PATH
CADDY_UI_CADDY_ROOT_CONFIG
CADDY_UI_CADDY_BINARY
CADDY_UI_PORTAL_UPSTREAM
CADDY_UI_CADDY_COMMAND_TIMEOUT_SECONDS
CADDY_UI_ALLOW_CUSTOM_ROUTES
```

### Auth

```text
Security:RequireTotp=false
DataProtection:PersistKeysToPostgreSql=true
Database:ApplyMigrationsOnStartup=false
```

Abweichungen von sicheren Defaults werden sichtbar dokumentiert und getestet.

## 12. Rollen

- `viewer`: read-only Betriebs-, Analytics- und Detailansichten
- `editor`: Viewer plus Routenentwurf, Preview, Shadow/Apply entsprechend Betriebsmodus
- `admin`: Editor plus Domains, Provider, Zugriffsgruppen und Sicherheit

Analytics-Seiten bleiben GET/read-only. Schreibende Aktionen verwenden POST, CSRF-Schutz, Audit und klare Bestätigung.

## 13. CI und Verifikation

```sh
dotnet restore CaddyUi.slnx
dotnet format CaddyUi.slnx --verify-no-changes
dotnet build CaddyUi.slnx --configuration Release --no-restore
dotnet test CaddyUi.slnx --configuration Release --no-build

docker build --file Dockerfile.dotnet --target dotnet-companion .
docker build --file Dockerfile.dotnet --target dotnet-bundle .

gofmt -w cmd caddyguard caddynetcp
go test ./...
```

CI verifiziert mindestens:

- Restore und Format
- Release-Build ohne Warnungen
- Unit-, Web-, Infrastruktur-, Migrations- und Acceptance-Tests
- PostgreSQL-Migrationen
- Companion- und Bundle-Container
- HTTP-Healthflächen
- SQLite-Migrations-CLI
- integriertes Caddy-Modul
- Routenvalidierung und Compiler-Golden-Master
- geschützte Phase-7-Webflächen

## 14. Produktionsisolation und Rollback

Bis Phase 9:

- Python-/SQLite bleibt produktive Quelle und Schreibpfad
- .NET läuft intern oder im Shadow-Betrieb
- Analytics-Ingestion ist standardmäßig aus
- Route Write Mode ist standardmäßig `disabled`
- Caddy, DNS und produktive Blocklist werden nicht automatisch umgestellt
- keine Portumschaltung ohne Wartungsfenster

Rollback:

- .NET-Worker/UI deaktivieren
- Python-Container mit unveränderter SQLite-Datei weiterverwenden
- Caddy-Dateien aus Snapshot wiederherstellen
- Phase-7-Apply verwendet eigenen automatischen Rollback

## 15. Offene Produktionsvalidierung

- längerer Last-/Burstlauf mit echten Caddy-Logs
- Shadow-Vergleich der Statistiken gegen Python
- Logrotation, Truncate und Neustart
- Entscheidung über First-Party-Beacons
- kontrollierter RIPEstat-Test
- kontrollierter Blocklist-Test
- Speicher-, Partitions- und Retentionprüfung
- vollständiger LAN-/Public-/Portal-Sicherheitstest
- Zuordnung aller Legacy-Routen ohne Domain
- Caddyfile-Golden-Master gegen Python
- Shadow-Apply mit echten Routen
- kontrollierter Reload-/Rollbacktest
- Dateirechte und Persistenz der generierten Fragmente

## 16. Nächste Phasen

### Phase 8 – DNS, DDNS und Systemfunktionen

- produktive Provider-API-Clients
- Wildcard-/DNS-01-Renderer
- Netcup DNS und DDNS-Jobs
- E-Mail, Webhook, Discord und Telegram
- Backups und Diagnoseexport
- Jobübersicht
- Public- und Upstream-Health

### Phase 9 – Shadow-Betrieb und Umschaltung

- paralleler interner Betrieb
- Statistik- und Routingvergleich
- Dry-Run-Migration
- Wartungsfenster und finaler SQLite-Import
- Umschaltung von 8098/8099
- vollständige Login-, Route-, DNS- und Statistikverifikation

### Phase 10 – Python entfernen

Erst nach mindestens zwei stabilen Releases, abgeschlossener Migration und geprüftem Rollback:

- Python-Laufzeit entfernen
- Legacy-Hotfixmodule und SQLite-Schreibpfade entfernen
- Images verkleinern
- Dokumentation finalisieren
- Version `2.0.0` veröffentlichen

## 17. Fortsetzungscheckliste

1. richtigen gestapelten Branch und PR-Head prüfen
2. diese Übergabe und betroffene Fachverträge lesen
3. vorhandene Implementierung und Tests zuerst prüfen
4. sichere Defaults und Python-Isolation erhalten
5. Änderungen sauber in Domain, Application, Infrastructure und Web einordnen
6. Unit-, Integrations-, Web-, Migrations- und Containerpfade ergänzen
7. Statusdokument und diese Übergabe aktualisieren
8. UI gegen `UI_DESIGN_CONTRACT.md` prüfen
9. CI vollständig grün abwarten
10. erst danach PR aus Draft nehmen oder nächste Phase stapeln

Keine Änderung gilt als abgeschlossen, solange Build, Tests, Migration und Container-Smoke nicht verifiziert sind.
