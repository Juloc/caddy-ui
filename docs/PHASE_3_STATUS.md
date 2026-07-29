# Phase 3 – Authentifizierung, Rollen, Portal und Domain-Grundlage

Status: implementiert und verifiziert  
Branch: `agent/dotnet-postgres-phase-3`  
Basis: `agent/dotnet-postgres-phase-2`

## Authentifizierung

- getrennte Admin-Cookies für direkten LAN-Zugriff und öffentlichen Proxy-Zugriff
- separater Access-Portal-Sitzungsspeicher und Portal-Cookies pro Access Group
- serverseitige Sitzungsvalidierung in PostgreSQL
- User-Agent-Bindung
- Rollen Administrator, Editor und Viewer
- TOTP und einmalige Recovery-Codes
- persistente Data-Protection-Verschlüsselung vorhandener TOTP-Secrets
- PBKDF2-SHA256 für neue Kennwörter
- Verifikation und automatischer Rehash vorhandener Python-scrypt-Kennwörter
- persistente Login-Versuche und progressive Sperren
- Origin-, Referer-, Sec-Fetch-Site- und Antiforgery-Prüfung
- Rollen- und Benutzernamensänderungen machen bestehende Autorisierungsclaims ungültig

## LAN, Public und Portal

- direkter Zugriff auf private IP-Adressen über Port `8098` bleibt möglich
- Public Admin benötigt den exakten HTTPS-Origin und `X-Caddy-Admin-Secret`
- Portalzugriff ist auf Port `8099` und `X-Caddy-Portal-Secret` begrenzt
- Forwarded Header werden nur innerhalb einer validierten Proxy-Surface ausgewertet
- `CADDY_UI_REQUIRE_TOTP=false` bleibt gültig
- öffentlicher Betrieb ohne verpflichtendes TOTP erzeugt Log- und UI-Warnungen
- Portal-Metadatenzugriffe wie `/favicon.ico` erzeugen keinen versteckten Loginzustand

## Domains und Zertifikate

- mehrere verwaltete Domains
- Routen erhalten `domain_id`, `subdomain` und `certificate_mode`
- Wildcard ist der Datenbank- und UI-Standard jeder neuen Domain
- Routen verwenden standardmäßig `inherit`
- Einzelzertifikate erfordern eine explizite Auswahl
- Wildcards werden fachlich nur für genau eine Subdomain-Ebene akzeptiert
- bestehende Legacy-Routen bleiben bis zur Domain-Reconciliation nullable

## Provider-Management

- 18 gängige DNS-Provider im Katalog
- Provider können zentral angelegt, aktiviert und Domains zugeordnet werden
- Konfiguration und Secret-Referenzen werden getrennt gespeichert
- Secretwerte werden nicht in PostgreSQL oder Auditdaten übernommen
- Live-API-Clients und DNS-/DDNS-Jobs bleiben Bestandteil von Phase 8

## Verifikation

- Restore, Format, Release-Build und alle Tests erfolgreich
- PostgreSQL-Migration einschließlich Domain- und Provider-Schema erfolgreich
- Companion-Container mit Admin- und Portal-Port erfolgreich
- SQLite-Import im Companion-Container erfolgreich
- Bundle-Container und integriertes Caddy-Guard-Modul erfolgreich
- direkter LAN-Login-Endpunkt und getrennte Portal-Surface erfolgreich geprüft
- bestehender Python-Workflow weiterhin erfolgreich
- Branch auf einen konsolidierten Implementierungscommit reduziert

## Isolation

- der Python-Produktivpfad bleibt aktiv
- Caddy-Schreibzugriffe werden noch nicht auf .NET umgestellt
- Provider-Management in Phase 3 verändert keine produktiven DNS-Zonen
- die neue Authentifizierung ist auf dem separaten .NET-Preview-Port testbar
