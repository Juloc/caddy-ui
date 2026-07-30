# Caddy UI 2.0 – Entwicklungsübergabe

Status: .NET-Produktionsarchitektur vollständig implementiert  
Version: `2.0.0`  
Standardbranch: `main`

## Zielstack

- .NET 10
- ASP.NET Core Razor Pages
- EF Core / Npgsql
- PostgreSQL 17
- Caddy mit Netcup-DNS- und Guard-Modul
- servergerenderte AE01-orientierte Fluent-/Windows-11-Oberfläche
- kein SPA-Framework und kein Docker-Socket

## Projekte

```text
src/CaddyUi.Contracts       Transport- und Statusverträge
src/CaddyUi.Domain          Fachtypen und Invarianten
src/CaddyUi.Application     Klassifikation, Compiler und Regeln
src/CaddyUi.Infrastructure  PostgreSQL, Provider, Worker, Dateien und Cutover
src/CaddyUi.Web             Razor Pages, Authentifizierung und CSS
src/CaddyUi.Migration       idempotenter SQLite-Import

tests/*                     Unit-, Web-, PostgreSQL- und Acceptance-Tests
caddyguard                  integrierter Routenschutz
caddynetcp                  Netcup-DNS-Modul
deploy                      versionierte Produktions- und archivierte Shadow-Vorlagen
```

## Implementierter Funktionsumfang

- Rollen, LAN-/Public-Admin, TOTP und Recovery-Codes
- getrenntes Access Portal
- Domains und Provider
- Proxy-, Redirect-, Static- und Custom-Routen
- deterministischer Caddyfile-Compiler
- Preview, Diff, Validate, Apply, Verify und Rollback
- DNS, DDNS, Healthchecks und geplante Jobs
- Request-, Pageview-, Session- und Clientanalyse
- IP Intelligence, Risikoanalyse und Blocklisten
- Benachrichtigungen, Backups und Diagnose
- Legacy-SQLite-Import und kontrollierter Produktionsübergang

## Produktionsstart

Die kanonische Vorlage ist `deploy/docker-compose.yml`. Sie startet in dieser Reihenfolge:

1. PostgreSQL
2. EF-Core-Schemamigration
3. idempotenter Legacy-SQLite-Import
4. Vorbereitung und Validierung der Caddy-Konfiguration
5. Caddy
6. Caddy UI und Betriebsworker

Das vorhandene `ui-data`-Volume wird read-only als `/legacy` eingebunden. Neue Daten liegen in `postgres-data` und `ui-state`.

## Routenübergang

`prepare-production-config.sh` ersetzt unspezifische Wildcard-Imports durch den exakten Root-Import:

```text
/etc/caddy/routes/site-managed-routes.caddy
```

Bestehende `site-*.caddy`-Dateien werden nach `legacy-dotnet-cutover` verschoben. Die neue Managed-Datei importiert sie zunächst weiter. Dadurch bleibt die laufende Konfiguration bis zum ersten erfolgreichen .NET-Apply identisch und kann über den gespeicherten Snapshot zurückgerollt werden.

Die Datei `/etc/caddy/routes/site-security-blocks.caddy` ist trotz des historischen Namens kein Caddyfile. Sie ist ein separater Laufzeitfeed im Format `IP|Ablauf|Grund` und wird nicht in die Root-Konfiguration importiert.

## Caddy-Steuerung

Validierung läuft lokal im Bundle. Reloads laufen über `/usr/local/bin/caddy-remote` an `CADDY_ADMIN_URL=http://caddy:2019`. Der Caddy-Admin-Port bleibt ausschließlich im Docker-Netz erreichbar.

## Release

`VERSION_DOTNET` ist die Versionsquelle. Die Stable-Pipeline:

1. prüft Versionsvertrag, Format, Build und Tests;
2. veröffentlicht Bundle und Companion;
3. prüft Module, Migration, Konfigurationsübergang und Health;
4. erstellt Tag und GitHub Release;
5. öffnet den Deployment-PR in `Juloc/docker`.

Der alte Beta-Publisher ist archiviert. `deploy/shadow` bleibt nur als isolierter Diagnose- und Vergleichsstack erhalten.

## Verbindliche Regeln

- keine Secrets in PostgreSQL-Datensätzen, Logs, Reports oder Diffs
- Provider verwenden nur Secret-Referenzen
- produktive Dateischreibvorgänge atomisch und serialisiert
- Caddy-Reload nur nach vollständiger Validierung
- automatische Rückkehr zum vorherigen Snapshot bei Apply-Fehlern
- IP-Blockfeed niemals als Caddyfile importieren
- Portalport `8099` nie auf dem Host veröffentlichen
- kein Zugriff auf `/var/run/docker.sock`
- SQLite und Legacy-Routen bis zur bestätigten Serverabnahme nicht löschen

## Prüfkommandos

```sh
dotnet restore CaddyUi.slnx
dotnet format CaddyUi.slnx --no-restore --verify-no-changes
dotnet build CaddyUi.slnx --configuration Release --no-restore
dotnet test CaddyUi.slnx --configuration Release --no-build
python -m unittest discover -v
go test ./...
docker compose -f deploy/docker-compose.yml config
```

Die verbleibende Hostprüfung ist Betrieb, kein offener Entwicklungsumfang: Containerstatus, Login, Routen, Zertifikate, Importbericht und ein kontrollierter Apply müssen nach dem Deployment geprüft werden.
