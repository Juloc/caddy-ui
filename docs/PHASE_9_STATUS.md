# Phase 9 – Produktionsumschaltung

Status: Repository-Implementierung abgeschlossen  
Zielversion: `2.0.0`  
Produktionsstack: .NET 10 / Razor Pages / PostgreSQL 17

## Abgeschlossen

- produktive, versionierte .NET-Bundle- und Companion-Images
- PostgreSQL-Schemamigration vor jedem Start
- idempotenter Import der vorhandenen read-only SQLite-Datei
- persistente PostgreSQL-, Betriebs- und Legacy-Volumes
- reversibler Übergang der vorhandenen `site-*.caddy`-Routen
- exakte Root-Caddyfile-Imports für verwaltete Routen und IP-Blockliste
- Remote-Reload gegen den separaten Caddy-Container
- aktive Routing-, DNS-, DDNS-, Worker- und Blocklist-Modi
- Netcup-Wildcard-Renderer und integriertes Guard-Modul
- Produktions-Compose-Vertrag und Container-Smokes in CI
- stabile Release-Pipeline mit Tag, GitHub Release und Deployment-PR
- archivierter, unveränderlicher Shadow-Beta-Stack als Diagnoseoption

## Sicherheits- und Rückfallgrenzen

- Die alte SQLite-Datei bleibt read-only erhalten.
- Bestehende Route-Dateien werden nicht gelöscht, sondern nach `legacy-dotnet-cutover` verschoben.
- Vor dem ersten .NET-Apply importiert die neue verwaltete Datei die alten Routen unverändert.
- Caddy startet nur nach erfolgreicher Validierung der vollständigen Konfiguration.
- PostgreSQL, SQLite, alte Routen und neue Betriebsdaten verwenden getrennte Volumes.
- Der UI-Container besitzt keinen Docker-Socket und keine Linux-Capabilities.
- Port `8099` bleibt intern.

## Verifikation

Die Pull-Request-Prüfungen müssen vor Integration bestehen:

- Python-/Legacy-Vertragstests
- Go-Formatierung und Modultests
- .NET Restore, Format, Release-Build und Tests
- Companion- und Bundle-Containerstart
- Admin-Login und Portaloberfläche
- SQLite-Import
- Compose-Rendering
- Legacy-Routen-Bridge und Caddy-Validierung

## Externe Betriebsprüfung

Die Repository-Implementierung kann den tatsächlichen Host nicht selbst bestätigen. Nach Übernahme der generierten Compose-Datei sind auf dem Server nur noch folgende Laufzeitnachweise erforderlich:

- Container `postgres`, `caddy` und `caddy-ui` gesund
- `/health/live` und `/health/ready` erfolgreich
- öffentlicher und lokaler Admin-Login erfolgreich
- bestehende Domains und Routen erreichbar
- PostgreSQL-Importbericht ohne Fehler
- Caddy-Reload und ein kontrollierter Route-Apply erfolgreich

Diese Prüfungen ändern keinen Quellcode und sind kein offener Entwicklungsumfang.
