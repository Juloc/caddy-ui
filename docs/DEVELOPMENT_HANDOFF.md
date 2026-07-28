# Caddy UI 2.0 – Entwicklungsübergabe

Status: verbindliche zentrale Arbeitsgrundlage  
Aktueller Entwicklungszweig: `agent/dotnet-postgres-phase-6`  
Produktiver Stand: Python/SQLite bleibt bis zur kontrollierten Umschaltung aktiv

Dieses Dokument bündelt die Informationen, die für die Weiterentwicklung des .NET-/PostgreSQL-Neubaus benötigt werden. Die einzelnen `PHASE_*_STATUS.md`- und Vertragsdokumente bleiben als detaillierte Nachweise bestehen. Bei Widersprüchen gelten die Sicherheits- und Fachverträge sowie die zuletzt in CI verifizierte Implementierung.

## 1. Produktziel

Caddy UI ist eine kompakte servergerenderte Verwaltungs- und Beobachtungsoberfläche für Caddy. Der Neubau ersetzt die Python-/SQLite-Anwendung schrittweise durch .NET 10, Razor Pages und PostgreSQL, ohne den produktiven Caddy-Betrieb während der Entwicklung zu gefährden.

Zielbereiche:

- Admin- und Access-Portal-Authentifizierung
- Domains, DNS-Provider und Zertifikatsgrundlage
- echte Request-, Pageview-, Session- und Clientstatistik
- IP Intelligence, Bot- und Risikobewertung
- Routen, Zugriffsschutz und kontrollierter Caddy-Schreibpfad
- DNS, DDNS, Benachrichtigungen, Backups und Diagnose
- kontrollierter Shadow-Betrieb, Migration und Umschaltung

Nicht Teil des Produkts:

- Docker-Socket-Verwaltung
- App-Templates
- eine schwere SPA-Laufzeit

## 2. Zielarchitektur

```text
Browser
  -> Caddy
      -> Admin UI auf 8098
      -> internes Access Portal auf 8099
      -> verwaltete Upstreams

Caddy JSON Access Logs
  -> dateibasierter Tailer
  -> Parser und Redaction
  -> Request-Klassifikation
  -> PostgreSQL Batch Writer
  -> Navigation, Pageview, Page Load und Session
  -> Aggregate und Read-only UI

Caddy UI .NET
  -> Razor Pages
  -> Application/Domain-Verträge
  -> Infrastructure Stores und Worker
  -> PostgreSQL
```

Containergrenze:

- `dotnet-companion`: UI/Portal/Jobs ohne gebündelten Caddy
- `dotnet-bundle`: gleiches UI-Image plus eigener Caddy-Build mit integrierten Modulen
- kein Docker-Socket
- Caddy-Admin-Port und Portal-Port dürfen nicht öffentlich veröffentlicht werden

## 3. Repository-Struktur

```text
src/CaddyUi.Contracts       transport- und statusnahe Verträge
src/CaddyUi.Domain          fachliche Typen und Invarianten
src/CaddyUi.Application     fachliche Services, Klassifikation und Regeln
src/CaddyUi.Infrastructure  PostgreSQL, Provider, Worker und Dateischreibpfade
src/CaddyUi.Web             Razor Pages, Auth-Middleware und statische Assets
src/CaddyUi.Migration       idempotenter SQLite-/Legacy-Import

tests/*                     Unit-, Web-, Infrastruktur-, Migrations- und Acceptance-Tests
caddyguard                  integriertes Caddy-Schutzmodul
caddynetcp                  Netcup-DNS-Modul
docs                        Verträge, Status und diese Übergabe
```

## 4. Branch- und PR-Kette

Die Phasen sind absichtlich gestapelt. Eine spätere Phase basiert auf dem Branch der vorherigen Phase, nicht direkt auf `main`.

| Phase | Branch | PR | Inhalt |
| --- | --- | --- | --- |
| 1 | `agent/dotnet-postgres-phase-1` | #20 | Solution, Razor-Grundlage, Docker und CI |
| 2 | `agent/dotnet-postgres-phase-2` | #21 | PostgreSQL-Schema und Legacy-Migration |
| 3 | `agent/dotnet-postgres-phase-3` | #22 | Authentifizierung, Domains und Provider |
| 4 | `agent/dotnet-postgres-phase-4` | #23 | Log-Ingestion und echte Statistik |
| 5 | `agent/dotnet-postgres-phase-5` | #24 | IP Intelligence, Risiko und Blockierung |
| 6 | `agent/dotnet-postgres-phase-6` | nächster Draft-PR | Read-only Statistikoberfläche |

Vor einem Merge auf `main` muss die Kette entweder der Reihe nach integriert oder sauber auf die bereits integrierte Basis umgestellt werden. Keine Phase darf durch einen unkontrollierten Rebase fachliche Sicherheitsänderungen verlieren.

## 5. Bereits implementierte Phasen

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
- persistente progressive Login-Sperren
- Origin-/Referer-/CSRF-Prüfung
- Domains, DNS-Provider-Katalog und Wildcard-Zertifikat als Standard
- `CADDY_UI_REQUIRE_TOTP=false` bleibt erlaubt und wird öffentlich sichtbar gewarnt

### Phase 4 – Analytics-Ingestion

- Caddy-JSON-Parser
- Secret-, Header- und Query-Redaction vor der Speicherung
- File-Tailer mit Byte-Checkpoint und Rotationserkennung
- transaktionaler und idempotenter Import
- pseudonyme Clientkennung über persistierten geschützten HMAC-Schlüssel
- Requests, Navigationen, Pageviews, Page Loads und Sessions
- Stunden-, Tages-, Monats- und Routenaggregate
- Retention und Partitionswartung
- standardmäßig deaktivierter Shadow-Pfad

### Phase 5 – IP Security

- kanonische IPv4-/IPv6-Normalisierung
- lokale Scope-Erkennung
- kein externer Lookup für private, reservierte oder Dokumentationsnetze
- RIPEstat Network Info und AS Overview im Hintergrund
- Cache und exponentielles Fehler-Backoff
- deterministische versionierte Risikoengine `risk-v1`
- Reasons und Evidence je Bewertung
- Clientliste und Detailseite
- manuelles Blockieren und Entsperren
- Pflichtgrund, Ablaufzeit und maximale Sperrdauer
- atomische Blocklist mit Verifikation und Rollback
- Security-, History- und Audit-Einträge mit Correlation-ID
- Betriebsmodi `disabled`, `shadow`, `active`

### Phase 6 – Read-only UI

- Dashboard mit fachlichen Nutzungs- und technischen Lastkennzahlen
- globale URL-basierte Filter für Zeitraum, Host, Akteur, Requesttyp und Statusklasse
- Traffic, Besucher/Clients, Requests und Routenanalyse
- Bots & Sicherheit sowie Fehler & Performance
- Live-Log über Server-Sent Events
- System- und Ingestionstatus
- responsive, servergerenderte Oberfläche ohne SPA-Framework
- keine schreibende Caddy-Aktion aus den neuen Analytics-Seiten

## 6. Verbindlicher Statistikvertrag

### Request

Jeder vom Server verarbeitete HTTP-Request zählt genau einmal. Dazu gehören HTML, JavaScript, CSS, Bilder, Fonts, APIs, WebSocket-Upgrades, Healthchecks, Redirects, Bots und Fehler. Diese Kennzahl beschreibt die technische Serverlast.

### Navigation

Eine Navigation ist ein mutmaßlicher Browser-Dokumentaufruf. Evidenz wird gewichtet:

1. First-Party-Pageview-Beacon
2. `Sec-Fetch-Dest: document`
3. HTML-Accept-/Content-Type-Merkmale
4. Methode `GET` oder `HEAD`
5. kein Asset-, API-, Healthcheck- oder interner Pfad
6. Akteur spricht nicht für Bot oder internen Zugriff

### Pageview

Ein Pageview entsteht ausschließlich bei:

- erfolgreicher menschlicher Dokumentnavigation mit `2xx` oder `304`
- bestätigtem SPA-Routenwechsel über ein optionales First-Party-Beacon

Redirects bleiben Requests und Navigationen, erzeugen aber keinen eigenen Pageview. Dokumentfehler erscheinen als fehlgeschlagene Navigation, nicht als erfolgreicher Pageview.

### Page Load

Ein Page Load gruppiert einen Pageview mit zeitlich und fachlich zugehörigen Requests. Die Gruppierung über Client, Host, Referer, Zielpfad und Zeitfenster ist eine technische Näherung. Daraus entstehen Requests, Bytes, Asset-/API-Anteil und Ladezeit je Pageview.

### Session

Eine Session endet standardmäßig nach 30 Minuten Inaktivität. Bots und interne Requests erzeugen keine normalen Besuchersessions.

### Client und Visitor

- `Client`: pseudonymer HMAC-Schlüssel aus Proxy-/Browsermerkmalen; ohne Beacon ausdrücklich geschätzt
- `Visitor`: optionaler First-Party-Identifier eines Beacons

Die UI darf einen geschätzten Client niemals als sicher identifizierte Person oder exakten Unique Visitor ausweisen.

### Beispiel Mealie/Nuxt

Ein Dokumentaufruf plus 100 JavaScript-/Nuxt-Assets ergibt:

- 101 Requests
- 1 Navigation
- 1 Pageview
- 1 Page Load
- 100 Asset-Requests

`human + asset` ist ein technischer Asset-Request und niemals ein Pageview. Gehashte Frameworkdateien werden in Routenanalysen normalisiert und nicht als Top-Seiten behandelt.

### Klassifikationsdimensionen

```text
ActorType:
  human | bot | internal | unknown

RequestType:
  document | asset | api | websocket | healthcheck | auth | system | other

ClassificationConfidence:
  high | medium | low
```

Akteur, Requesttyp und Risiko sind getrennte Dimensionen.

## 7. Sicherheitsvertrag

- Public Admin und Access Portal sind getrennte Sicherheitsoberflächen.
- Portal- und Admin-Cookies sowie Sitzungen dürfen nicht vermischt werden.
- eingehende interne Identitätsheader werden entfernt oder kontrolliert überschrieben.
- Secrets dürfen weder in Datenbank-Rohlogs noch in Preview, Diff, Diagnose oder UI erscheinen.
- private und reservierte IPs dürfen niemals an RIPEstat gesendet werden.
- eine Risikobewertung ist ein Hinweis und keine sichere Identifikation.
- automatische produktive IP-Sperren bleiben standardmäßig deaktiviert.
- Netzsperren werden nicht in ein Format geschrieben, das der aktuelle Caddy-Guard nicht unterstützt.
- aktive Dateiänderungen müssen atomar, verifiziert und rückrollbar sein.
- jede Sperr-/Entsperraktion braucht Audit und Correlation-ID.

## 8. PostgreSQL und Migrationen

Wichtige Tabellen:

- `users`, `admin_sessions`, `portal_sessions`, `login_attempts`, `login_blocks`
- `managed_domains`, `dns_providers`, `managed_routes`, `route_revisions`
- `anonymous_clients`, `analytics_sessions`
- partitionierte `request_events`
- `navigation_events`, `page_views`, `page_loads`
- `hourly_traffic_aggregates`, `daily_traffic_aggregates`, `monthly_traffic_aggregates`
- `route_performance_aggregates`
- `analytics_checkpoints`, `ingestion_failures`
- `ip_intelligence_cache`, `client_assessments`, `client_assessment_reasons`
- `security_events`, `ip_block_rules`, `ip_block_history`, `audit_events`
- `scheduled_jobs`, `job_runs`

Regeln:

- jede Schemaänderung erhält eine neue Migration; alte Migrationen werden nach Veröffentlichung nicht umgeschrieben
- Migrationen müssen gegen leere PostgreSQL-Datenbanken und den Legacy-Import getestet werden
- Raw Requests sind partitioniert und retentionfähig
- Import und Aggregate müssen idempotent bleiben
- keine unbeschränkte In-Memory-Warteschlange

## 9. Konfiguration und sichere Defaults

### Analytics

```text
Analytics:Enabled=false
Analytics:BatchSize=1000
Analytics:PollIntervalMilliseconds=1000
Analytics:SessionIdleMinutes=30
Analytics:PageLoadWindowSeconds=15
Analytics:RawRequestRetentionDays=30
Analytics:PageViewRetentionDays=180
Analytics:HourlyRetentionDays=90
Analytics:DailyRetentionDays=730
```

Wichtige Umgebungsvariablen:

```text
CADDY_UI_ANALYTICS_ENABLED
CADDY_UI_LOG_PATHS
CADDY_UI_INGEST_BATCH_SIZE
CADDY_UI_INGEST_FLUSH_MS
CADDY_UI_SESSION_IDLE_MINUTES
CADDY_UI_RAW_REQUEST_RETENTION_DAYS
CADDY_UI_PAGEVIEW_RETENTION_DAYS
```

### IP Security

```text
IpSecurity:IntelligenceEnabled=false
IpSecurity:RiskAssessmentEnabled=false
IpSecurity:BlockWriteMode=disabled
IpSecurity:SuccessCacheHours=24
IpSecurity:FailureCacheMinutes=10
IpSecurity:MaximumBlockHours=720
```

Betriebsmodi:

- `disabled`: Datenbank/Audit ohne Dateiänderung
- `shadow`: separate Preview-Datei ohne produktive Wirkung
- `active`: konfigurierte Caddy-Guard-Blocklist

### Auth

```text
Security:RequireTotp=false
DataProtection:PersistKeysToPostgreSql=true
Database:ApplyMigrationsOnStartup=false
```

Eine bewusste Abweichung von sicheren Defaults muss sichtbar dokumentiert und getestet werden.

## 10. Rollen und Berechtigungen

- `viewer`: alle read-only Betriebs-, Analytics- und Detailansichten
- `editor`: Viewer-Rechte plus kontrollierte fachliche Änderungen wie manuelle IP-Sperren
- `admin`: Sicherheits-, Domain-, Provider- und spätere Systemadministration

Analytics-Seiten verwenden ausschließlich GET und read-only Stores. Schreibende Formulare dürfen nicht unbemerkt in Statistikseiten eingebaut werden.

## 11. UI-Vertrag

- Razor Pages, kein schweres SPA-Framework
- globale Filter bleiben in der URL und funktionieren mit Zurück/Vorwärts
- Pageviews und Requests werden nie in einer Kennzahl vermischt
- Top-Seiten stammen aus Pageviews; Assets und APIs werden separat dargestellt
- geschätzte Clients werden sichtbar als geschätzt markiert
- Server-Sent Events nur für Live-Log und Status; keine dauernden Vollseitenabfragen
- responsive Desktop-/Mobilansicht
- System, Light und Dark über Design Tokens bzw. Systemfarbschema
- dichte Tabellen für Requests und Routen; Cards nur für Kernkennzahlen

## 12. CI und lokale Verifikation

Verbindliche Prüfungen:

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

CI muss mindestens verifizieren:

- Restore und Format
- Release-Build ohne Warnungen
- alle Unit-/Web-/Infrastruktur-/Migrations-/Acceptance-Tests
- PostgreSQL-Migrationen
- Companion- und Bundle-Container
- HTTP-Healthflächen
- SQLite-Migrations-CLI
- integriertes Caddy-Modul im Bundle

`TreatWarningsAsErrors`, aktuelle Analyzer, deterministische Builds und Code-Style-Prüfung sind aktiviert.

## 13. Produktionsisolation und Rollback

Bis Phase 9 gilt:

- Python-/SQLite-Anwendung bleibt produktive Quelle und Schreibpfad
- .NET läuft intern oder im Shadow-Betrieb
- Analytics-Ingestion ist standardmäßig aus
- Caddy-Konfiguration, DNS-Zonen und produktive Blocklist werden nicht automatisch umgestellt
- keine Portumschaltung von 8098/8099 ohne kontrolliertes Wartungsfenster

Rollback je Phase:

- .NET-Worker oder UI deaktivieren
- Checkpoints beibehalten oder bewusst zurücksetzen
- Python-Container mit unveränderter SQLite-Datei weiterverwenden
- Caddy-Dateien und Blocklist nicht verändern oder aus Snapshot wiederherstellen

## 14. Offene Produktionsvalidierung

Vor einer Umschaltung müssen noch erfolgen:

- längerer Last- und Burstlauf mit echten Caddy-Logs
- Shadow-Vergleich der Statistiken gegen Python
- Prüfung von Logrotation, Truncate und Containerneustart
- Entscheidung, welche SPAs ein First-Party-Beacon erhalten
- kontrollierter RIPEstat-Test mit öffentlichen Testadressen
- kontrollierter Shadow- und aktiver Blocklist-Test mit absichtlicher Test-IP
- Prüfung von Speicherverbrauch, Partitionspflege und Retention
- vollständiger Security-Test für LAN, Public Origin und Portal

## 15. Nächste Phasen

### Phase 7 – Routen, Zugriff und Caddy-Schreibpfad

- Routen-CRUD, Upstreams und Healthchecks
- Access Groups
- Preview und Diff
- Validate, Apply, Reload, Verify und Rollback
- unveränderliche Revisionen
- Import, Export und kontrollierte Custom Routes

Abnahme: Golden-Master-Ausgaben stimmen; Fehler stellen vorherige Konfiguration wieder her; keine Secrets in Diff oder Logs.

### Phase 8 – DNS, DDNS und Systemfunktionen

- Netcup DNS und DDNS-Jobs
- E-Mail, Webhook, Discord und Telegram
- Backups, Diagnoseexport und Jobübersicht
- Public- und Upstream-Health

### Phase 9 – Shadow-Betrieb und Umschaltung

- paralleler interner Betrieb
- gleiche Logs read-only verarbeiten
- Statistikvergleich und Dry-Run-Migration
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

## 16. Fortsetzungscheckliste

Vor jeder neuen Arbeit:

1. richtige gestapelte Basis und aktuellen PR-Head prüfen
2. diese Übergabe sowie betroffene Fachverträge lesen
3. vorhandene Implementierung und Tests zuerst prüfen
4. sichere Defaults und Python-Isolation erhalten
5. Änderung in vorhandene Schichten einordnen
6. Unit-, Integrations-, Web- und Containerpfade ergänzen
7. Statusdokument und diese Übergabe aktualisieren
8. CI vollständig grün abwarten
9. erst danach PR aus Draft nehmen oder nächste Phase darauf stapeln

Keine Änderung gilt als abgeschlossen, solange Build, Tests, Migration und Container-Smoke nicht verifiziert sind.
