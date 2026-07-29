# Phase 2 – PostgreSQL-Schema und SQLite-Migration

Status: implementiert und CI-verifiziert  
Branch: `agent/dotnet-postgres-phase-2`  
Basis: `agent/dotnet-postgres-phase-1`

## Enthalten

- vollständiges PostgreSQL-Zielschema für:
  - Benutzer, Rollen, TOTP, Recovery Codes und Sitzungen
  - Access Groups und Portal-Zugänge
  - Routen, Revisionen, Apply-Schritte und Caddy-Snapshots
  - DNS-Provider
  - Audit, Benachrichtigungen und Jobs
  - Rohrequests, Navigationen, Pageviews, Page Loads und Sessions
  - Stunden-, Tages-, Monats- und Performanceaggregate
  - IP Intelligence, Clientbewertungen, Security Events und Sperren
  - Klassifikations- und Botregeln
  - Migrationsläufe, Importnachweise und unverändert erhaltene Legacy-Zeilen
- monatlich partitionierte Tabelle `request_events`
- Funktion zum idempotenten Anlegen neuer Request-Partitionen
- persistente ASP.NET-Core-Data-Protection-Keys in PostgreSQL
- verschlüsselte Übernahme vorhandener TOTP-Secrets; kein neuer Klartextspeicher
- konsistente SQLite-Sicherung über die SQLite-Backup-API
- SHA-256-basierte Quellidentifikation
- Inventar aller SQLite-Tabellen, Spalten, Primärschlüssel und Zeilenanzahlen
- idempotenter Import bekannter Tabellen
- JSON-Erhaltung unbekannter Tabellen ohne Datenverlust
- bewusster Ausschluss aktiver Admin-/Portal-Sitzungen und Route-Previews
- optionaler Import vorhandener Rohrequest-Tabellen
- Dry-Run, Importbericht und Verifikation über Importnachweise
- Integrations- und Container-Smoke-Tests
- explizit in `public` gespeicherte EF-Migrationshistorie, damit der gleichnamige
  Anwendungsschema- und Datenbankbenutzer `caddy_ui` keine abweichende
  `search_path`-Auflösung verursacht

## Verifikation

Erfolgreich geprüft:

- Restore ohne bekannte verwundbare Paketversion
- Formatprüfung
- Release-Build ohne Warnungen
- alle Unit-, Web-, PostgreSQL- und Migrationstests
- vollständige PostgreSQL-Migration auf einer leeren Datenbank
- wiederholter Migrationsaufruf nach bereits angewendetem Schema
- konsistentes SQLite-Backup
- Import, zweiter idempotenter Import und Verifikation
- Companion-Image mit Health-, Readiness- und Migration-CLI-Smoke
- Bundle-Image mit integriertem Caddy-Guard-Modul
- bestehende Python- und Go-Verifikation

## CLI

```sh
# PostgreSQL-Schema anwenden
caddy-ui migrate schema

# SQLite nur untersuchen
caddy-ui migrate inspect \
  --source /var/lib/caddy-ui/caddy-ui.db \
  --report /var/lib/caddy-ui/migration-inspection.json

# Import ohne Änderungen planen
caddy-ui migrate import \
  --source /var/lib/caddy-ui/caddy-ui.db \
  --dry-run \
  --report /var/lib/caddy-ui/migration-plan.json

# Konsistente Sicherung erstellen und importieren
caddy-ui migrate import \
  --source /var/lib/caddy-ui/caddy-ui.db \
  --backup-dir /var/lib/caddy-ui/backups \
  --report /var/lib/caddy-ui/migration-report.json

# Den beim Import erzeugten konsistenten Backupstand verifizieren
caddy-ui migrate verify \
  --source /var/lib/caddy-ui/backups/caddy-ui-<timestamp>.db \
  --report /var/lib/caddy-ui/migration-verify.json
```

## Importregeln

| SQLite-Tabelle | PostgreSQL-Ziel |
| --- | --- |
| `users` | `users` |
| `settings` | `application_settings` |
| `providers` | `dns_providers` |
| `routes` | `managed_routes` |
| `access_groups` | `access_groups` |
| `access_credentials` | `access_credentials` |
| `revisions` | `route_revisions` |
| `audit_events` | `audit_events` |
| `notifications` | `notifications` |
| `traffic_buckets` | Stunden-/Tages-/Monatsaggregate |
| `migration_state` | `legacy_migration_state` |
| unbekannte persistente Tabellen | `legacy_source_rows` |
| `sessions`, `portal_sessions`, `route_previews` | absichtlich nicht übernommen |

Jede importierte oder erhaltene Zeile bekommt einen Eintrag in
`legacy_import_keys`. Dadurch kann die Verifikation Quellzeilen und
PostgreSQL-Nachweise vergleichen.

## Abgrenzung

- Die Python-Anwendung bleibt der aktive Produktivpfad.
- Es erfolgt noch keine produktive Umschaltung auf PostgreSQL.
- Sitzungen werden nicht übernommen; bei der späteren Umschaltung ist eine
  erneute Anmeldung erforderlich.
- Fachliche Services für Authentifizierung, Statistik, IP Intelligence und
  Caddy-Schreibzugriffe werden erst in den folgenden Phasen auf das Schema
  geschaltet.
