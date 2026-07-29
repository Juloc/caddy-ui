# Phase 7 – Routen, Zugriff und kontrollierter Caddy-Schreibpfad

Status: implementiert und in CI verifiziert  
Branch: `agent/dotnet-postgres-phase-7`  
Basis: `agent/dotnet-postgres-phase-6`

## Implementiert

- typisiertes Routenmodell für Proxy, Redirect, statische Antwort und optional Custom Caddyfile
- Domain-, Subdomain-, Host-, Pfad-, Port- und Injektionsvalidierung
- Routenliste und kompakter Routen-Editor
- Reihenfolge, Aktivstatus, Zertifikatsmodus und optionale Zugriffsgruppe
- Zugriffsgruppen und gehashte Portalzugänge
- deaktivierte Zugriffsgruppen sperren neue und bestehende Portal-Sitzungen
- deterministischer Caddyfile-Compiler
- unveränderliche Revisionen mit Manifest und SHA-256-Digest
- Zeilen-Diff zwischen aktuellem Fragment und Kandidat
- Schreibmodi `disabled`, `shadow` und `active`
- Kandidatenvalidierung über Caddy
- atomischer Fragmentaustausch
- vollständige Validierung, Reload und Nachprüfung im Active-Modus
- automatische Wiederherstellung bei Fehlern
- manueller Rollback des letzten erfolgreichen Apply
- serialisierte Apply-/Rollback-Operationen
- Operationsschritte, Snapshots und Auditdaten
- standardmäßig deaktivierte Custom Routes
- produktive Sperre für Wildcard-/Inherit-Zertifikate bis Phase 8
- portabler JSON-Export ohne Kennwörter, Provider-Secrets oder Sitzungen
- validierter atomarer Routenimport mit Schema-, Domain-, Gruppen- und Konfliktprüfung

## UI

Die Oberfläche folgt einem verbindlichen AE01-inspirierten Fluent-Arbeitsstil:

- klare neutrale Hintergrundfläche und sichtbar abgegrenzte Arbeitsflächen
- dunkle gruppierte Sidebar
- kompakte Topbar und feste Statusbar
- 34-px-Controls, 30-px-Kompaktcontrols
- eindeutige Primär-, Sekundär- und Gefahraktionen
- sichtbare Input-Rahmen und Fokuszustände
- flache Arbeitsbereiche statt Card-Wänden
- kompakte Tabellen mit klaren Zeilenaktionen
- kein Gradient, Glow, Blur oder schweres UI-Framework
- responsive Darstellung und Reduced-Motion-Unterstützung
- überarbeitete Admin- und Portal-Loginflächen
- klarer Routing-Editor, Diff-Ansicht und Import-/Export-Arbeitsbereich

Verbindlicher Vertrag: `docs/UI_DESIGN_CONTRACT.md`.

## Verifikation

- Restore und Format erfolgreich
- Release-Build ohne Warnungen erfolgreich
- Unit-, Web-, PostgreSQL- und Migrationsprüfungen erfolgreich
- Compiler-, Diff-, Route-Validation- und Zertifikatsschutztests erfolgreich
- Routenexport/-import und transaktionaler Konflikt-Rollback erfolgreich
- geschützte Routing-, Transfer- und Access-Seiten verlangen Authentifizierung
- Companion-Container, HTTP-Flächen und SQLite-Migrations-CLI erfolgreich
- Bundle-Container und integriertes Caddy-Modul im vollständigen Workflow geprüft

## Sichere Defaults

- `Routing:WriteMode=disabled`
- `Routing:AllowCustomRoutes=false`
- produktive Caddy-Datei wird ohne explizite Aktivierung nicht verändert
- Shadow- und Active-Pfade sind getrennt
- Python bleibt produktiver Schreibpfad
- Wildcard-/Inherit-Revisionen können in Phase 7 nicht aktiv angewendet werden
- Import erzeugt nur PostgreSQL-Entwürfe und führt keinen Apply aus

## Produktionsvalidierung

- generierter Caddyfile-Golden-Master gegen den Python-Bestand
- Shadow-Lauf mit realen Routen
- Root-Import-, Reload- und Rollbacktest in kontrollierter Umgebung
- Zuordnung aller Legacy-Routen ohne `domain_id`
- Dateirechte und Persistenz der verwalteten Fragmente
- vollständiger Wildcard-/DNS-01-Renderer aus Phase 8

## Nächste Phase

Phase 8 ergänzt DNS-/DDNS-Provider, Wildcard-Zertifikatsrenderer, Jobs, Benachrichtigungen, Backups und Diagnose.
