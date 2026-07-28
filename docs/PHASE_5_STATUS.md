# Phase 5 – IP Intelligence, Bot-Erkennung und Blockierung

Status: Implementierung begonnen  
Branch: `agent/dotnet-postgres-phase-5`  
Basis: `agent/dotnet-postgres-phase-4`

## Implementiert

- kanonische IPv4-/IPv6-Normalisierung
- lokale Scope-Erkennung ohne externen Lookup für private und reservierte Bereiche
- RIPEstat Network Info und AS Overview
- Hintergrundqueue, Cache, Fehlercache und exponentielles Backoff
- versionierte und deterministische Bot-/Risikobewertung mit Reasons und Evidence
- Clientliste und Clientdetailseite
- IP-Intelligence-, Bewertungs-, Request- und Blockhistorie im Clientdetail
- manuelles Blockieren und Entsperren für Editor und Administrator
- Grund und Ablaufzeit sind Pflicht
- atomisches Blocklist-Schreiben mit Verifikation und Rollback
- Security Events, Block-History und Audit Events mit Correlation-ID
- Betriebsmodi disabled, shadow und active

## Isolation

- externe IP Intelligence ist standardmäßig deaktiviert
- Risiko-Worker ist standardmäßig deaktiviert
- Blocklist-Schreibmodus ist standardmäßig disabled
- der bestehende Python-Produktivpfad bleibt unverändert aktiv
- keine produktive Caddy-Konfiguration wird in Phase 5 automatisch umgestellt

## Noch ausstehend

- CI-Verifikation
- Shadow-Vergleich mit realen Produktionslogs
- kontrollierter aktiver Blocklist-Test mit absichtlich blockierter Test-IP
