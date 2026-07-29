# Caddy UI 2.0 – kontrollierte Produktionsumschaltung

Dieses Runbook beschreibt den Wechsel von Python/SQLite auf .NET 10, Razor Pages und PostgreSQL. Es enthält absichtlich keine automatische Port-, DNS- oder Routingumschaltung. Jede produktive Änderung bleibt eine sichtbare, einzeln prüfbare Aktion.

## 1. Unveränderliche Sicherheitsregeln

- Python/SQLite bleibt bis zum bestätigten Wartungsfenster produktiv.
- Die Legacy-SQLite-Datei wird im .NET-Container ausschließlich read-only eingehängt.
- Beide Anwendungen lesen im Shadow-Betrieb dieselben Caddy-JSON-Access-Logs.
- Vor der Umschaltung bleiben Routing, DNS und Blocklist `disabled` oder `shadow`.
- Betriebsworker bleiben bis nach der Portumschaltung deaktiviert.
- Keine Secretwerte in Reports, Manifeste, Diffs, Logs oder Diagnosearchive schreiben.
- Bei einem Blocker wird nicht improvisiert, sondern zurückgefallen.

## 2. Shadow-Betrieb

Erforderliche Konfiguration des internen .NET-Containers:

```text
CADDY_UI_ANALYTICS_ENABLED=true
CADDY_UI_LOG_PATHS=/logs/access.json
CADDY_UI_LEGACY_SQLITE_PATH=/data/caddy-ui/legacy/caddy-ui.db
CADDY_UI_LEGACY_STATISTICS_PATH=/data/caddy-ui/cutover/legacy-statistics.json
CADDY_UI_CUTOVER_MANIFEST_DIR=/data/caddy-ui/cutover
CADDY_UI_ROUTE_WRITE_MODE=shadow
CADDY_UI_BLOCKLIST_WRITE_MODE=shadow
CADDY_UI_CUTOVER_ENABLED=false
```

`Operations:DnsWriteMode` bleibt `disabled` oder `shadow`; `Operations:WorkerEnabled` bleibt `false`.

Der Shadow-Lauf muss mindestens die konfigurierte Zeit abdecken. Standard sind 24 Stunden. Neustart, Logrotation und Checkpoint-Fortsetzung müssen während dieses Zeitraums beobachtet werden.

## 3. Legacy-Statistik-Snapshot

Python/SQLite und .NET werden nur über dasselbe geschlossene UTC-Zeitfenster verglichen. Der Snapshot liegt standardmäßig unter `/data/caddy-ui/cutover/legacy-statistics.json`.

```json
{
  "capturedAt": "2026-07-29T18:00:00Z",
  "windowStart": "2026-07-28T18:00:00Z",
  "windowEnd": "2026-07-29T18:00:00Z",
  "requests": 34800,
  "pageViews": 1250,
  "sessions": 470,
  "clients": 340,
  "errors": 132
}
```

Definitionen:

- `requests`: alle serverseitig verarbeiteten Requests
- `pageViews`: erfolgreiche menschliche Dokumentnavigationen beziehungsweise bestätigte SPA-Wechsel
- `sessions`: Legacy-Sitzungen im Zeitfenster
- `clients`: pseudonyme technische Clients, keine garantierten Personen
- `errors`: HTTP-Status `>= 500`

Die .NET-Seite **Administration → Umschaltung** berechnet die gleichen Kennzahlen direkt aus PostgreSQL. Standardtoleranz sind 5 Prozent je Kennzahl.

## 4. Readiness-Gate

Vor Beginn des Wartungsfensters müssen mindestens folgende Prüfungen bestanden sein:

- PostgreSQL erreichbar
- keine ausstehenden EF-Core-Migrationen
- Legacy-SQLite lesbar und per SHA-256 identifiziert
- gemeinsame Caddy-Logs vollständig lesbar
- Mindestdauer des Shadow-Laufs erreicht
- Ingestion höchstens 15 Minuten hinter dem letzten Request
- kein produktiver Routing-, DNS- oder Blocklist-Schreibmodus aktiv
- Administratorkonto und Domains importiert
- aktuelles erfolgreiches PostgreSQL-Backup
- Statistikvergleich innerhalb der Toleranz
- persistentes Manifestverzeichnis beschreibbar

`CADDY_UI_CUTOVER_ENABLED=true` ist die letzte explizite Freigabe und wird nur für das Wartungsfenster gesetzt. Danach wird ein unveränderliches Readiness-Manifest erzeugt.

## 5. Wartungsfenster vorbereiten

1. Nutzer informieren und Änderungssperre setzen.
2. Produktive Python-Schreibzugriffe stoppen, aber Container für den Rückfall nicht löschen.
3. Caddy-Konfiguration und aktuelle Route-Snapshots sichern.
4. Konsistentes SQLite-Backup erstellen.
5. PostgreSQL-Custom-Backup erstellen und Digest prüfen.
6. Readiness- und Statistikmanifest extern sichern.

## 6. Finaler Legacy-Import

Die Quelle ist immer das konsistente Backup, nie die noch veränderliche Originaldatei.

```sh
caddy-ui migrate inspect \
  --source /data/caddy-ui/cutover/legacy-final.db \
  --report /data/caddy-ui/cutover/legacy-inspect.json

caddy-ui migrate import \
  --source /data/caddy-ui/cutover/legacy-final.db \
  --backup-dir /data/caddy-ui/backups \
  --report /data/caddy-ui/cutover/legacy-import.json \
  --dry-run

caddy-ui migrate import \
  --source /data/caddy-ui/cutover/legacy-final.db \
  --backup-dir /data/caddy-ui/backups \
  --report /data/caddy-ui/cutover/legacy-import-final.json

caddy-ui migrate verify \
  --source /data/caddy-ui/cutover/legacy-final.db \
  --report /data/caddy-ui/cutover/legacy-verify.json
```

Import oder Verify mit einem anderen Digest blockiert die Umschaltung.

## 7. Portumschaltung

Die bestehenden Caddy-Upstreams werden kontrolliert geändert:

```text
Admin UI     -> .NET :8098
Access Portal -> .NET :8099
```

Vorgehen:

1. Kandidatenkonfiguration rendern.
2. Diff prüfen.
3. `caddy validate` ausführen.
4. vorherigen Snapshot festhalten.
5. atomisch anwenden und Caddy reloaden.
6. öffentliche und interne Healthchecks prüfen.

Keine DNS-, DDNS-, Blocklist- oder Route-Worker gleichzeitig aktivieren.

## 8. Abnahmetests nach der Umschaltung

In dieser Reihenfolge prüfen:

1. `/health/live` und `/health/ready`
2. Admin-Login und Logout
3. TOTP und Recovery-Code
4. Access-Portal-Login
5. Rollen `admin`, `editor`, `viewer`
6. Domain- und Providerübersicht
7. Routenliste, Preview und Diff
8. bestehende öffentliche Routen
9. Wildcard- und Einzelzertifikate
10. DNS-Read und Provider-Test ohne Write
11. Request-, Pageview-, Session- und Clientzahlen
12. Live-Log und Logrotation
13. Backup und Diagnoseexport
14. Secret-Leak-Prüfung

Erst nach bestandener Abnahme werden Worker und Schreibmodi einzeln aktiviert und jeweils erneut geprüft.

## 9. Rückfallkriterien

Sofort zurückfallen bei:

- Login oder Portal nicht verfügbar
- falscher Rollen- oder Surface-Schutz
- fehlenden beziehungsweise falschen Routen
- Zertifikats- oder Caddy-Reload-Fehlern
- Datenverlust oder nicht erklärbarer Statistikabweichung
- Secretwerten in Logs, UI, Manifest oder Diagnose
- nicht behebbarer PostgreSQL- oder Worker-Störung

## 10. Rückfall

1. .NET-Betriebsworker deaktivieren.
2. Routing, DNS und Blocklist auf `disabled` setzen.
3. vorherigen Caddy-Snapshot wiederherstellen und validieren.
4. Upstreams wieder auf Python/SQLite stellen.
5. Python-Container starten beziehungsweise Schreibsperre entfernen.
6. SQLite-Datei unverändert weiterverwenden.
7. PostgreSQL und alle Phase-9-Manifeste für die Fehleranalyse behalten.
8. Ereignis, Zeitstempel, Digest und Ursache dokumentieren.

Der Rückfall löscht keine PostgreSQL-Daten und überschreibt keine Legacy-Datei.

## 11. Abschlussnachweise

Für die Produktionsfreigabe werden dauerhaft aufbewahrt:

- Legacy-Quell-Digest
- SQLite-Backup
- PostgreSQL-Backup und Digest
- Inspect-, Dry-Run-, Import- und Verify-Report
- Readiness-Manifest
- Statistikvergleich
- Caddy-Konfigurationssnapshot vor und nach der Umschaltung
- Abnahmeprotokoll
- dokumentierter Rückfalltest
