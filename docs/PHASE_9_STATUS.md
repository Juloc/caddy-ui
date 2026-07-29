# Phase 9 – Shadow-Betrieb und Umschaltung

Status: implementiert und in CI verifiziert  
Branch: `agent/dotnet-postgres-phase-9`  
Basis: `agent/dotnet-postgres-phase-8`  
Draft-PR: #28

## Implementiert

- explizit deaktivierter Cutover-Modus als sicherer Standard
- zentrale Readiness-Prüfung für PostgreSQL, Migrationen und Inventar
- SHA-256-Identifikation der read-only Legacy-SQLite-Datei
- Prüfung gemeinsamer Caddy-Logpfade
- Mindestdauer und Aktualität des Shadow-Laufs
- Blockierung bei vorzeitig aktiven Routing-, DNS- oder Blocklist-Schreibmodi
- Prüfung eines aktuellen PostgreSQL-Backups
- Statistikvergleich über dasselbe geschlossene UTC-Zeitfenster
- konfigurierbare Abweichungstoleranz je Kennzahl
- unveränderliche Readiness- und Statistikmanifeste
- administratorgeschützte Razor Page für Readiness, Vergleich und Wartungsreihenfolge
- sichtbarer Cutover-Status in Navigation, Topbar und Statusbar
- vollständiges Produktions- und Rollback-Runbook
- Authentifizierungstest für den neuen Arbeitsbereich
- Unit-Tests für Statistikvergleich und Snapshotvalidierung

## Sichere Defaults

```text
Cutover:Enabled=false
Analytics:Enabled=false
Operations:WorkerEnabled=false
Operations:DnsWriteMode=disabled
Routing:WriteMode=disabled
IpSecurity:BlockWriteMode=disabled
```

Die Phase führt keine automatische Port-, DNS-, Zertifikat-, Routen- oder Workerumschaltung durch. Python/SQLite bleibt bis zum expliziten Wartungsfenster produktiv.

## Readiness-Blocker

Die Umschaltung bleibt blockiert bei:

- fehlender expliziter Freigabe
- nicht erreichbarem PostgreSQL oder ausstehenden Migrationen
- fehlender beziehungsweise nicht lesbarer Legacy-SQLite-Datei
- deaktivierter oder veralteter Shadow-Ingestion
- zu kurzer Shadow-Laufzeit
- aktiven produktiven Schreibmodi vor der Abnahme
- fehlendem Administratorkonto oder fehlenden Domains
- fehlendem beziehungsweise veraltetem PostgreSQL-Backup
- fehlendem oder abweichendem Statistik-Snapshot
- nicht beschreibbarem Manifestverzeichnis

## Statistikvertrag

Der Legacy-Snapshot enthält für ein geschlossenes UTC-Zeitfenster:

- Requests
- Pageviews
- Sessions
- Clients
- HTTP-5xx-Fehler

.NET berechnet dieselben Werte direkt aus PostgreSQL. Standardmäßig darf jede Kennzahl höchstens 5 Prozent abweichen.

## CI-Verifikation

- `Verify`: Lauf `30471183169`, erfolgreich
- `Verify .NET rebuild`: Lauf `30471183630`, erfolgreich
- Restore und kanonische Formatprüfung erfolgreich
- Release-Build ohne Warnungen erfolgreich
- Unit-, Web-, PostgreSQL- und Migrationssuite erfolgreich
- Statistikvergleich und Snapshotvalidierung erfolgreich getestet
- neue Cutover-Seite ist authentifizierungspflichtig
- Companion-Container gebaut, gestartet und per HTTP geprüft
- Bundle-Container gebaut, gestartet und inklusive Caddy-Modul geprüft
- SQLite-Migrations-CLI im Companion-Pfad geprüft

## Noch offene Produktionsvalidierung

- Shadow-Lauf mit echten Caddy-Logs
- finaler Dry-Run und Verify auf einer produktionsnahen SQLite-Kopie
- Statistikvergleich gegen einen echten Legacy-Snapshot
- Backup und Test-Wiederherstellung
- vollständiger Login-, Portal-, Routen-, DNS- und Zertifikatstest
- kontrollierte Portumschaltung
- dokumentierter Rückfalltest

## Nächste Phase

Phase 10 entfernt Python, SQLite-Schreibpfade und Legacy-Hotfixmodule erst nach mindestens zwei stabilen .NET-Releases.
