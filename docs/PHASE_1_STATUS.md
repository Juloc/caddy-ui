# Phase 1 – .NET-Grundgerüst und CI

Status: implementiert und verifiziert  
Branch: `agent/dotnet-postgres-phase-1`  
Basis: `agent/dotnet-postgres-rebuild-plan`

## Enthalten

- .NET-10-Solution im SLNX-Format
- Projekte für Domain, Application, Contracts, Infrastructure, Web und Migration
- Testprojekte für Unit-, Integrations-, Web-, Migrations- und Acceptance-Tests
- zentrale Analyzer-, Nullable-, Format- und Paketversionseinstellungen
- EF-Core-DbContext mit initialer PostgreSQL-Migration
- PostgreSQL-Integrationstest über Testcontainers
- Razor-Pages-Grundlayout mit AE01-naher Seitenleiste, Topbar, Karten und Design Tokens
- strukturierte JSON-Konsolenlogs
- `/health/live` ohne Datenbankabhängigkeit
- `/health/ready` einschließlich PostgreSQL
- optionaler Migrationslauf beim Start
- separate `dotnet-companion`- und `dotnet-bundle`-Dockerziele
- unveränderter bestehender Python-Dockerfile und unveränderte Go-Module
- eigener CI-Workflow für Restore, Format, Build, Tests und Container-Smoke

## Verifikation

GitHub Actions prüft:

- Restore, Formatierung und Build ohne Warnungen
- Unit-, Web-, Migrations-, Acceptance- und PostgreSQL-Integrationstests
- Start mit PostgreSQL und automatischer EF-Core-Migration
- Liveness- und Readiness-Endpunkte
- Razor-Pages-Übersicht
- Companion- und Bundle-Container
- vorhandenes Caddy-Guard-Modul im Bundle
- weiterhin bestehende Python- und Go-Prüfungen

Die Workflows `Verify` und `Verify .NET foundation` waren auf dem Phase-1-Stand erfolgreich.

## Lokale Verifikation

```sh
dotnet restore CaddyUi.slnx
dotnet format CaddyUi.slnx --verify-no-changes --no-restore
dotnet build CaddyUi.slnx --configuration Release --no-restore
dotnet test CaddyUi.slnx --configuration Release --no-build

docker build -f Dockerfile.dotnet --target dotnet-companion -t caddy-ui:dotnet-companion-phase1 .
docker build -f Dockerfile.dotnet --target dotnet-bundle -t caddy-ui:dotnet-bundle-phase1 .
```

## Abgrenzung

Diese Phase schaltet keine produktive Route, Anmeldung, Portal-Funktion, Statistik oder Caddy-Schreibfunktion auf .NET um. Der bestehende Python-Produktivpfad bleibt aktiv. Das vollständige PostgreSQL-Schema und der SQLite-Import gehören zu Phase 2.
