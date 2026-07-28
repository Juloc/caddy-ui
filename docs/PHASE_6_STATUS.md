# Phase 6 – Read-only Razor-Pages-Oberfläche

Status: implementiert und in CI verifiziert  
Branch: `agent/dotnet-postgres-phase-6`  
Basis: `agent/dotnet-postgres-phase-5`

## Implementiert

- zentraler read-only `AnalyticsReadStore`
- URL-basierte globale Filter für Zeitraum, Host, Akteur, Requesttyp und Statusklasse
- Dashboard mit Pageviews, fehlgeschlagenen Navigationen, Sessions, geschätzten Clients, Requests, Requests pro Pageview, Traffic, Fehlerquote, p95 und Bot-Anteil
- getrennte Zeitreihen für Requests und Pageviews
- Top-Seiten ausschließlich aus Pageviews
- separate API- und Asset-Auswertungen
- Traffic-Seite
- Besucher- und Clientseite mit sichtbarer Schätzkennzeichnung
- Requesttabelle
- normalisierte Routenanalyse
- Bots- und Security-Übersicht
- Fehler- und Performanceansicht
- Live-Log über autorisierte Server-Sent Events
- System-, Ingestion-, Retention- und Feature-Flag-Status
- responsive CSS-Erweiterung ohne Chart- oder SPA-Framework

## Verifikation

- `Verify`: erfolgreich, Lauf `30399050081`
- `Verify .NET rebuild`: erfolgreich, Lauf `30399050095`
- Restore, Format, Release-Build und alle Tests: erfolgreich
- PostgreSQL-Integrationstest für getrennte Pageviews und Asset-Requests: erfolgreich
- Companion- und Bundle-Container: erfolgreich
- Health-, SQLite-Migrations- und integrierter Caddy-Modulpfad: erfolgreich
- alle read-only Seiten und der SSE-Endpunkt verlangen Authentifizierung

## Fachliche Grenzen

- die neuen Analytics-Seiten sind read-only
- Actor, Requesttyp und Risiko bleiben getrennte Dimensionen
- Clients werden nicht als sichere Personen dargestellt
- Assets werden nicht als Pageviews oder Top-Seiten gezählt
- SPA-Routenwechsel werden ohne optionales Beacon nicht erfunden

## Isolation

- `Analytics:Enabled` bleibt standardmäßig `false`
- IP Intelligence und Risk Worker bleiben standardmäßig deaktiviert
- Blocklist-Modus bleibt standardmäßig `disabled`
- Python, Caddy-Konfiguration, DNS-Zonen und produktive Routen bleiben unverändert

## Produktionsvalidierung

- längerer Lauf gegen reale Shadow-Daten
- UX-Prüfung mit großen Request- und Routendatenmengen
- optionales First-Party-Beacon wird weiterhin separat entschieden
