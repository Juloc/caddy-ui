# Caddy UI 2.0 – Produktionsumschaltung

Dieses Runbook gilt für den Wechsel vom Python-/SQLite-Stack auf die stabile .NET-/PostgreSQL-Version.

## Sicherheitsregeln

- Vor dem Deployment ein externes Backup der Volumes `etc`, `data`, `config`, `logs` und `ui-data` erstellen.
- Das alte `ui-data`-Volume nicht löschen oder umbenennen.
- Die produktive `.env` vor dem Wechsel sichern.
- Port `8099` nicht veröffentlichen.
- Bei fehlerhafter Caddy-Validierung, Migration oder Anmeldung zurückfallen.

## Vorbereitung

Erforderliche neue Werte in `.env`:

```env
CADDY_UI_DB_NAME=caddy_ui
CADDY_UI_DB_USER=caddy_ui
CADDY_UI_DB_PASSWORD=<zufällig>
CADDY_UI_ADMIN_PROXY_SECRET=<zufällig>
CADDY_UI_PORTAL_PROXY_SECRET=<zufällig>
CADDY_UI_PUBLIC_ORIGIN=https://caddy.juloc.de
```

Die vorhandenen Werte für Netcup, ACME, Admin-Benutzer und Admin-Passwort bleiben bestehen.

## Startreihenfolge

`deploy/docker-compose.yml` erzwingt automatisch:

1. PostgreSQL-Healthcheck
2. EF-Core-Schemamigration
3. read-only SQLite-Import
4. Vorbereitung der Routen-Bridge
5. vollständige Caddy-Validierung
6. Start von Caddy
7. Start der Razor-Pages-Anwendung

Der SQLite-Import ist idempotent. Der Importbericht liegt im Volume `ui-state` unter `/state/legacy-import.json`.

## Routen-Bridge

Beim ersten Start werden bisherige `/etc/caddy/routes/site-*.caddy`-Dateien nach
`/etc/caddy/routes/legacy-dotnet-cutover` verschoben. Die neue Datei
`site-managed-routes.caddy` importiert diese alten Dateien zunächst weiter.

Dadurch bleiben alle bisherigen Hosts aktiv, bevor eine neue Route über die .NET-Oberfläche angewendet wird. Der erste erfolgreiche Apply ersetzt nur diese Bridge-Datei und speichert vorher einen Rollback-Snapshot.

## Abnahme

Nach dem Start prüfen:

```sh
docker compose ps
docker compose logs --no-color migrate legacy-import config-init caddy caddy-ui
curl --fail http://127.0.0.1:8098/health/live
curl --fail http://127.0.0.1:8098/health/ready
```

Danach in der Oberfläche prüfen:

- Admin-Login und Logout
- Benutzer, Domains und Provider
- bestehende Routen und Zugriffsschutz
- öffentliche Hosts und Zertifikate
- DNS-Lesezugriff und DDNS-Status
- Analytics-Ingestion
- Backup und Diagnoseexport
- Route-Preview und kontrollierter Apply

## Rückfall

Bei einem Blocker:

1. Den neuen Stack stoppen, ohne Volumes zu löschen.
2. Die vorherige Python-Compose-Datei wieder einsetzen.
3. Die alten Volumes `etc`, `data`, `config`, `logs` und `ui-data` unverändert verwenden.
4. Den Python-Stack starten.
5. Caddy-Konfiguration und öffentliche Hosts prüfen.

Die Legacy-SQLite-Datei und die verschobenen Routen bleiben unverändert vorhanden. PostgreSQL und `ui-state` werden für die Analyse behalten.

## Abschluss

Erst nach erfolgreichem Login, Importbericht, Routenprüfung, Zertifikatstest und mindestens einem erfolgreichen Apply gilt die Hostumschaltung als bestätigt. Die alte SQLite-Datei darf weiterhin als Rückfallnachweis aufbewahrt werden.
