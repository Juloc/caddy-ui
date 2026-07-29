# Phase 8 – DNS, DDNS und Systemfunktionen

Status: implementiert und in CI verifiziert  
Branch: `agent/dotnet-postgres-phase-8`  
Basis: `agent/dotnet-postgres-phase-7`

## Implementiert

- PostgreSQL-Persistenz für verwaltete DNS-Records und DDNS-Ziele
- Erweiterung der bereits vorhandenen Jobtabellen ohne destruktives Neuerstellen
- Benachrichtigungskanäle, geplante Jobs und Jobläufe
- öffentliche und interne Health-Ziele mit Verlauf
- Backup-Artefakte mit Manifest und Digest
- Secret-Referenzen über Umgebung oder absolute Secret-Datei
- direkte Provider-Adapter für Netcup, Cloudflare, DigitalOcean, Hetzner DNS, IONOS, Gandi, deSEC und DuckDNS
- explizite Provider-Verbindungstests
- DNS-Schreibmodi `disabled`, `shadow` und `active`
- DDNS mit A/AAAA, mehreren öffentlichen Adressdiensten und Unchanged-Erkennung
- Shadow-DDNS verändert den zuletzt produktiv geschriebenen Wert nicht
- PostgreSQL-basierte exklusive Job- und DDNS-Beanspruchung
- SMTP, HTTPS-Webhook, Discord und Telegram
- öffentliche und interne HTTP-Healthchecks
- PostgreSQL-Custom-Backups über `pg_dump`
- redigierter Diagnoseexport ohne Secretwerte und Secret-Referenzen
- Wildcard-/DNS-01-Renderer für das integrierte Netcup-Caddy-Modul
- domainbasierter Zertifikatsquellen-Cache
- kein stiller Fallback von Wildcard auf Einzelzertifikat
- kompakte AE01-inspirierte Razor Pages für DNS, DDNS, Jobs, Health, Benachrichtigungen und Backups
- Providerübersicht mit getrenntem Status für Management, direkte API und installiertes Caddy-DNS-Modul

## Sichere Defaults

```text
Operations:WorkerEnabled=false
Operations:DnsWriteMode=disabled
Routing:WriteMode=disabled
```

- kein DNS-Schreibzugriff ohne explizite Aktivierung
- kein automatisch laufender Job- oder DDNS-Worker ohne explizite Aktivierung
- Backups werden ausschließlich serverseitig in einem festen Verzeichnis abgelegt
- Secretwerte werden nicht in PostgreSQL, UI, Diagnose oder Manifest geschrieben
- Wildcard-Active-Apply bleibt blockiert, wenn Provider, Modul, Renderer oder Secret-Referenz nicht einsatzbereit sind
- Python/SQLite bleibt produktiver Pfad

## Provider-Matrix

Management unterstützt den vollständigen Katalog aus Phase 3. Direkte Record-API und DDNS sind in Phase 8 für folgende Provider implementiert:

- Netcup
- Cloudflare
- DigitalOcean
- Hetzner DNS
- IONOS
- Gandi
- deSEC
- DuckDNS

Der geprüfte Wildcard-Caddyfile-Renderer ist für Netcup aktiv, weil dieses Modul im Bundle enthalten ist. Andere Provider benötigen zusätzlich ein installiertes und geprüftes Caddy-DNS-Modul samt Rendererprofil.

## UI

- neutraler Anwendungshintergrund statt Weiß-auf-Weiß
- klar umrandete Arbeitsbereiche und Eingaben
- kompakte Tabellen statt Card-Wänden
- eindeutige Primär-, Sekundär- und Statusaktionen
- sichtbarer DNS-, Routen- und Worker-Modus in Topbar und Statusbar
- responsive Darstellung ohne SPA-Framework
- keine Gradienten, Glasflächen oder unnötigen Animationen

## CI-Verifikation

- Restore und kanonische Formatprüfung erfolgreich
- Release-Build ohne Warnungen erfolgreich
- Unit-, Web-, PostgreSQL-, Migrations- und Routentests erfolgreich
- Migration auf leerer PostgreSQL-Datenbank erfolgreich
- DNS-/DDNS-Persistenz und exklusive Beanspruchung erfolgreich getestet
- Netcup-Wildcard-Caddyfile und blockierte unvollständige Providerkonfiguration getestet
- alle neuen Managementseiten verlangen Authentifizierung
- Companion- und Bundle-Container erfolgreich gebaut und gestartet
- HTTP-, SQLite-Migrations- und integrierter Caddy-Modul-Smoke erfolgreich

## Noch offene Produktionsvalidierung

- Provider-Tests ausschließlich gegen kontrollierte Testzonen
- Shadow-DNS und DDNS gegen reale Provider
- Netcup-Wildcard-Zertifikat auf einer Testdomain
- Health-Fehlerbenachrichtigung über reale Kanäle
- Backup und Test-Wiederherstellung
- Diagnoseexport mit produktionsnahen Daten auf Secret-Leaks
- Worker-Sperre bei mehreren App-Instanzen

## Nächste Phase

Phase 9 führt den internen Shadow-Betrieb, Statistikvergleich, finalen Legacy-Import, die kontrollierte Portumschaltung und den vollständigen Produktionsabnahmelauf durch.
