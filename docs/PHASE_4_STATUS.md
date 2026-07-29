# Phase 4 – Log-Ingestion und echte Statistik

Status: implementiert und in CI verifiziert  
Branch: `agent/dotnet-postgres-phase-4`  
Basis: `agent/dotnet-postgres-phase-3`

## Fachlicher Vertrag

- Requests und Pageviews sind getrennte Größen.
- JavaScript-, CSS-, Bild-, Font- und Nuxt-Requests bleiben technische Requests.
- Ein erfolgreicher Dokumentaufruf erzeugt höchstens einen Pageview.
- Redirects erzeugen keinen eigenen Pageview.
- Akteur und Ressourcentyp werden unabhängig klassifiziert.
- geschätzte Clients werden ausdrücklich als geschätzt gespeichert.
- SPA-Navigationen benötigen für exakte Zählung ein optionales First-Party-Beacon.

Ein Dokumentaufruf mit 100 Nuxt-Assets ergibt 101 Requests, aber nur einen Pageview und einen Page Load.

## Implementierung

- Caddy-JSON-Access-Log-Parser
- Secret- und Query-Redaktion vor der Speicherung
- Klassifikation für Dokument, Asset, API, WebSocket, Healthcheck, Auth und System
- Human-, Bot-, Internal- und Unknown-Akteur
- pseudonyme Clientidentität mit persistiertem, Data-Protection-geschütztem HMAC-Schlüssel
- dateibasierter Tailer mit Byte-Checkpoint und Rotationserkennung
- transaktionaler und idempotenter Batchimport
- Request-, Navigation-, Pageview-, Page-Load- und Session-Erzeugung
- 30-Minuten-Sessions
- Stunden-, Tages-, Monats- und Routenaggregate
- Retention- und Partitionswartung
- standardmäßig deaktivierter Shadow-Ingestion-Pfad

## Verifikation

- `Verify`: erfolgreich
- `Verify .NET rebuild`: erfolgreich
- Restore, Format, Release-Build und Tests: erfolgreich
- PostgreSQL-Migration und Partitionsschema: erfolgreich
- Companion- und Bundle-Container: erfolgreich
- SQLite-Import und integriertes Caddy-Guard-Modul: erfolgreich
- Testfall `1 Dokument + 100 Nuxt-Assets`: 101 Requests, 1 Navigation, 1 Pageview, 1 Page Load und 100 Asset-Requests
- erneute Verarbeitung derselben Quellposition erzeugt keine doppelten Requests, Pageviews, Aggregate oder Sessions

## Produktionsvalidierung vor Umschaltung

- längerer Last- und Burstlauf mit realen Caddy-Logs
- Shadow-Vergleich gegen die bestehende Python-Statistik
- Festlegung, ob für einzelne SPAs ein First-Party-Beacon aktiviert wird
- read-only Statistikoberfläche folgt in Phase 6

## Isolation

- `Analytics:Enabled` ist standardmäßig `false`.
- Der bestehende Python-Produktivpfad bleibt aktiv.
- Caddy wird nicht verändert.
- Es werden keine Caddy-Konfigurationen oder DNS-Zonen geschrieben.
