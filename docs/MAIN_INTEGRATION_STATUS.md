# Caddy UI 2.0 – Main-Integrationsstatus

Stand: 29. Juli 2026

Die gestapelte Umbaukette ist vollständig in `main` integriert:

- PR #19 – verbindlicher Neuaufbauplan
- PR #20 – Phase 1: .NET-/PostgreSQL-Grundlage
- PR #21 – Phase 2: PostgreSQL-Schema und SQLite-Migration
- PR #22 – Phase 3: Authentifizierung, Domains und Provider
- PR #23 – Phase 4: Log-Ingestion und Statistik
- PR #24 – Phase 5: IP Intelligence und Security
- PR #25 – Phase 6: Read-only Analytics-UI
- PR #26 – Phase 7: Routing und kontrollierter Apply
- PR #27 – Phase 8: DNS, DDNS und Systemfunktionen
- PR #28 – Phase 9: Shadow-Readiness und kontrollierte Umschaltung

## Aktueller Betriebszustand

Der Code ist integriert, aber die produktive Laufzeit wurde nicht umgeschaltet. Python/SQLite bleibt bis zum kontrollierten Wartungsfenster produktiv.

Sichere Standardwerte bleiben verbindlich:

```text
Cutover:Enabled=false
Analytics:Enabled=false
Operations:WorkerEnabled=false
Operations:DnsWriteMode=disabled
Routing:WriteMode=disabled
IpSecurity:BlockWriteMode=disabled
```

## Release-Sperre

Automatische Releases nach jedem Push auf `main` sind während der Cutover-Validierung pausiert. Eine Veröffentlichung ist nur noch über einen bewussten manuellen `workflow_dispatch` möglich. Dadurch werden weder Images noch der produktive Docker-Stack versehentlich durch die reine Integrationsarbeit aktualisiert.

## Nächster Schritt

Vor Phase 10 ist ein echter Shadow-Betrieb erforderlich:

1. separate PostgreSQL-Datenbank starten;
2. .NET-Companion auf konfliktfreien internen Ports bereitstellen;
3. produktive Caddy-Logs ausschließlich read-only einbinden;
4. Legacy-SQLite ausschließlich read-only einbinden;
5. mindestens 24 Stunden Shadow-Daten sammeln;
6. Statistikvergleich und Readiness-Manifeste erzeugen;
7. Backup und Test-Wiederherstellung prüfen;
8. erst danach ein Wartungsfenster für Import und Portumschaltung planen.

Phase 10 bleibt gesperrt, bis mindestens zwei stabile .NET-Releases sowie ein dokumentierter Rückfalltest vorliegen.
