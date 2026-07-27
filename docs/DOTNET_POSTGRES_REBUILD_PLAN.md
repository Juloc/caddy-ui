# Caddy UI 2.0 – Plan für den Neuaufbau mit .NET, Razor Pages und PostgreSQL

Status: verbindlicher Umsetzungsplan  
Basis: `main` bei `f4892eb47a6859a1f08f3fca6c27ce19b002f7ce`  
Zielversion: `2.0.0`  

## 1. Ziel

Caddy UI wird schrittweise von der aktuellen Python-/SQLite-Anwendung auf den normalen Juloc-Stack umgebaut:

- .NET 10
- ASP.NET Core Razor Pages
- PostgreSQL
- EF Core und Npgsql
- serverseitig gerenderte Oberfläche
- kein SPA-Framework
- kein Node-Build für die Weboberfläche
- kleine ES-Module nur für Live-Aktualisierung, Dialoge und Diagramminteraktion
- weiterhin kein Docker-Socket-Zugriff

Der Umbau ersetzt nicht nur die Oberfläche. Statistik, Log-Verarbeitung, Sitzungen, Authentifizierung, IP-Auswertung, Sicherheitsregeln, Caddy-Konfiguration und Migration werden neu strukturiert.

## 2. Verbindliche Architekturentscheidungen

1. Die Anwendung bleibt eine serverseitig gerenderte Verwaltungsanwendung.
2. Razor Pages ist die einzige primäre UI-Technik.
3. PostgreSQL ist die einzige produktive Datenbank ab Version 2.0.
4. SQLite wird nur noch als Migrationsquelle gelesen.
5. Caddy und Caddy UI bleiben getrennte Prozesse und Container.
6. PostgreSQL ist ein eigener Dienst oder eine externe Instanz.
7. Es gibt keine direkte Docker-Steuerung und keinen Docker-Socket.
8. Die bestehenden Go-Caddy-Module bleiben zunächst unverändert.
9. Konfigurationsänderungen werden weiterhin validiert, atomar geschrieben, geladen, geprüft und bei Fehlern zurückgerollt.
10. Statistik trennt fachliche Nutzung von tatsächlicher Serverlast.
11. Die IP-Intelligence- und Bot-Bewertung aus `main` wird vollständig übernommen und erweitert.
12. Automatische IP-Sperren aufgrund einer Heuristik bleiben standardmäßig deaktiviert.
13. Direkter LAN-Zugriff und öffentlicher Zugriff werden getrennt behandelt.
14. `CADDY_UI_REQUIRE_TOTP=false` bleibt ein gültiger Betriebsmodus. Bei öffentlichem Zugriff wird deutlich gewarnt, aber die aktuelle Main-Funktionalität nicht stillschweigend verändert.
15. Bestehende Login-, CSRF-, Origin-, Portal- und LAN-Regressionsfälle werden vor der Umschaltung als Tests übernommen.

## 3. Bestehendes Verhalten, das erhalten bleiben muss

### 3.1 Bereitstellung

- Caddy läuft weiterhin separat vom Verwaltungsdienst.
- Der interne Caddy-Admin-Port `2019` wird nicht veröffentlicht.
- Der Portal-Port `8099` bleibt intern.
- Der Verwaltungsport `8098` kann im LAN direkt erreichbar sein.
- Die Anwendung darf weiterhin über eine lokale Adresse wie `http://192.168.x.x:8098` verwendet werden.
- Optionaler öffentlicher Zugriff erfolgt über den konfigurierten exakten Origin.
- Companion- und Bundle-Betrieb bleiben als Buildvarianten erhalten.

### 3.2 Caddy-Verwaltung

- Proxy-, Redirect- und Custom-Routen
- mehrere Upstreams
- Load-Balancing
- Healthchecks
- Header-, Pfad- und TLS-Optionen
- Vorschau und Diff
- Validierung
- Apply, Reload und Verify
- automatisches Rollback
- Revisionen und Audit-Protokoll
- aktivieren, deaktivieren, duplizieren, importieren und exportieren
- Access Groups und geschützte Routen
- DNS und Netcup-DDNS

### 3.3 Sicherheit

- Rollen Administrator, Editor und Viewer
- lokale und öffentliche Anmeldung
- optionale bzw. verpflichtende TOTP-Konfiguration
- getrennte Admin- und Portal-Sitzungen
- CSRF-Schutz
- Origin-/Referer-Prüfungen
- User-Agent-Bindung der Sitzungen
- progressive Login-Sperren
- manuelle temporäre IP-Sperren
- Security Events und Audit-Historie
- kein automatischer Trust gegenüber Proxy-Headern ohne bekannte Proxy-Kette

### 3.4 Neue IP-Funktionen aus `main`

Die folgenden Funktionen sind keine Übergangslösung und müssen in Caddy UI 2.0 enthalten sein:

- Erkennung privater, lokaler und spezieller Adressbereiche
- öffentliche ASN-, Präfix-, Registry- und Holder-Abfrage über RIPEstat
- Cache für erfolgreiche und fehlgeschlagene Abfragen
- Kennzeichnung, dass der Netzinhaber nicht mit der realen Person gleichzusetzen ist
- heuristische Bot-/Automatisierungsbewertung
- nachvollziehbare Gründe für die Bewertung
- Risiko- und Automatisierungsscore
- Client-Detailseite mit Requests, Endpunkten, Security Events und Sperraktion
- keine automatische Sperre allein aufgrund des Scores

## 4. Zielstruktur der Solution

```text
CaddyUi.sln
src/
  CaddyUi.Domain/
  CaddyUi.Application/
  CaddyUi.Infrastructure/
  CaddyUi.Web/
  CaddyUi.Migration/
  CaddyUi.Contracts/
tests/
  CaddyUi.Domain.Tests/
  CaddyUi.Application.Tests/
  CaddyUi.Infrastructure.Tests/
  CaddyUi.Web.Tests/
  CaddyUi.Migration.Tests/
  CaddyUi.AcceptanceTests/
cmd/
caddyguard/
caddynetcp/
docs/
```

### 4.1 Verantwortungen

#### `CaddyUi.Domain`

- Entitäten und Value Objects
- Rollen und Berechtigungen
- Routenmodell
- DNS-Modell
- Statistikbegriffe
- Request-Klassifikation
- Sicherheitsregeln
- IP-Risikoergebnis
- keine EF-Core-, HTTP- oder UI-Abhängigkeit

#### `CaddyUi.Application`

- Use Cases und Services
- Commands und Queries
- Transaktionsgrenzen
- Validierung
- Aggregationslogik
- Session- und Pageview-Erkennung
- Caddy-Apply-Orchestrierung
- Interfaces für Dateisystem, Caddy Admin API, DNS, Mail, Benachrichtigungen und IP Intelligence

#### `CaddyUi.Infrastructure`

- EF Core und PostgreSQL
- Npgsql-Batchimport
- Dateisystemzugriff
- Caddy Admin API
- RIPEstat-Client
- Netcup-Client
- E-Mail, Webhook, Discord und Telegram
- Datenexport und Backups
- Data-Protection-Key-Speicher

#### `CaddyUi.Web`

- Razor Pages
- View Components
- Layouts und Partials
- Cookie-Authentifizierung
- Antiforgery
- Autorisierungsrichtlinien
- Server-Sent Events
- statische Assets ohne Node-Build

#### `CaddyUi.Migration`

- SQLite-Import
- JSON-/Dateiimport
- Passwort- und Sessionmigration
- Dry-Run und Migrationsbericht
- ausschließlich als CLI und einmaliger Startup-Modus

#### `CaddyUi.Contracts`

- stabile DTOs für interne Grenzen
- Exportformate
- Importformate
- keine Datenbankentitäten nach außen

## 5. Laufzeit-Topologie

### 5.1 Standardbetrieb

```text
Browser
  -> Caddy :443
       -> verwaltete Anwendungen
       -> Caddy UI Web :8098
       -> Access Portal :8099

Caddy UI Web
  -> PostgreSQL :5432
  -> Caddy Admin API :2019
  -> Caddy-/Route-Dateien über vorhandene Volumes
  -> Caddy-Logs über Read-only-Logvolume
  -> RIPEstat und konfigurierte Benachrichtigungsdienste
```

### 5.2 Container

1. `caddy`
2. `caddy-ui`
3. `postgres` im Standard-Compose oder externe PostgreSQL-Instanz

Der Wechsel von zwei auf drei Standardcontainer ist eine bewusste Breaking Change für Version 2.0. Eine externe PostgreSQL-Instanz wird unterstützt, ein eingebetteter PostgreSQL-Prozess im UI-Container nicht.

### 5.3 Persistenz

- Caddy-Daten bleiben in den bestehenden Volumes.
- Caddy-Logs bleiben als Dateien verfügbar.
- UI-Daten liegen ausschließlich in PostgreSQL.
- Data-Protection-Keys erhalten ein eigenes persistentes Volume oder werden verschlüsselt in PostgreSQL gespeichert.
- Backups werden als `pg_dump`-kompatible Sicherungen und als anwendungsbezogene Exporte angeboten.

## 6. Datenmodell

### 6.1 Administration und Authentifizierung

- `Users`
- `Roles`
- `UserRoles`
- `UserTotpSettings`
- `AdminSessions`
- `PortalSessions`
- `LoginAttempts`
- `LoginBlocks`
- `DataProtectionKeys`

Admin- und Portal-Sitzungen verwenden getrennte Authentifizierungsschemas, Cookies und TTLs.

### 6.2 Caddy-Konfiguration

- `ManagedRoutes`
- `RouteUpstreams`
- `RouteHeaders`
- `RouteHealthChecks`
- `RouteTlsOptions`
- `AccessGroups`
- `AccessGroupUsers`
- `RouteAccessGroups`
- `RouteRevisions`
- `ApplyOperations`
- `ApplyOperationSteps`
- `CaddySnapshots`
- `AuditEvents`

### 6.3 DNS und DDNS

- `DnsProviders`
- `DnsZones`
- `DnsRecords`
- `DdnsJobs`
- `DdnsRuns`

Geheimnisse werden nicht im Klartext in fachlichen Tabellen abgelegt. Es werden Secret-Referenzen oder verschlüsselte Werte über ASP.NET Core Data Protection verwendet.

### 6.4 Rohdaten und Statistik

- `RequestEvents`
- `RequestClassifications`
- `NavigationEvents`
- `PageViews`
- `PageLoads`
- `AnalyticsSessions`
- `AnonymousClients`
- `HourlyTrafficAggregates`
- `DailyTrafficAggregates`
- `MonthlyTrafficAggregates`
- `RoutePerformanceAggregates`
- `ClientRiskSnapshots`
- `AnalyticsCheckpoints`
- `IngestionFailures`

### 6.5 IP und Sicherheit

- `IpIntelligenceCache`
- `ClientAssessments`
- `ClientAssessmentReasons`
- `SecurityEvents`
- `IpBlockRules`
- `IpBlockHistory`
- `ClassificationRules`
- `BotSignatureRules`

### 6.6 Partitionierung und Aufbewahrung

`RequestEvents` wird monatlich partitioniert. Partitionen werden durch einen Hintergrundjob angelegt und nach Ablauf der Aufbewahrung vollständig entfernt.

Standardwerte:

- Rohrequests: 30 Tage
- Navigations- und Pageview-Daten: 180 Tage
- Stundenaggregate: 90 Tage
- Tagesaggregate: 2 Jahre
- Monatsaggregate: unbegrenzt
- Security Events und Audit Events: konfigurierbar, Standard 2 Jahre
- IP-Intelligence-Cache: Erfolg 24 Stunden, Fehler 10 Minuten

Die Werte sind in der UI und über Umgebungsvariablen konfigurierbar.

## 7. Exakte Statistikbegriffe

### 7.1 Request

Jeder vom Server verarbeitete HTTP-Request zählt genau einmal als Request, einschließlich:

- HTML
- JavaScript
- CSS
- Bilder
- Fonts
- API
- WebSocket-Upgrade
- Healthchecks
- Redirects
- Bots
- Fehler

Diese Zahl beschreibt die tatsächliche Serverlast.

### 7.2 Navigation

Eine Navigation ist ein mutmaßlicher Aufruf eines Dokuments durch einen Browser. Sie wird anhand einer gewichteten Evidenz erkannt:

1. explizites Pageview-Beacon
2. `Sec-Fetch-Dest: document`
3. `Accept` enthält `text/html`
4. Methode `GET` oder `HEAD`
5. Pfad ist kein Asset, API-, Healthcheck- oder interner Systempfad
6. User-Agent und Client-Klassifikation sprechen nicht für einen Bot

Redirects bleiben Requests. Die nachfolgende erfolgreiche Dokumentantwort erzeugt die Pageview. Ein Redirect wird nicht als eigener Pageview gezählt.

### 7.3 Pageview

Ein Pageview entsteht bei:

- erfolgreicher menschlicher Dokumentnavigation mit Status `2xx` oder `304`
- bestätigtem SPA-Routenwechsel über das optionale Beacon

Dokumentantworten mit `4xx` oder `5xx` werden als fehlgeschlagene Navigation ausgewiesen, aber nicht als erfolgreicher Pageview.

### 7.4 Page Load

Ein Page Load gruppiert den Pageview mit den direkt zugehörigen Requests. Die Zuordnung nutzt:

- anonymen Client-Schlüssel
- Host
- Referer
- Zielpfad
- Zeitfenster

Die Gruppierung ist als technische Näherung gekennzeichnet. Daraus werden berechnet:

- Requests pro Pageview
- Bytes pro Pageview
- Asset-Anteil
- API-Anteil
- Ladezeit bis zum letzten zugeordneten Request

### 7.5 Session

Eine Session endet nach 30 Minuten Inaktivität. Der Wert ist konfigurierbar.

### 7.6 Client und Besucher

Ohne Beacon kann aus Serverlogs keine reale Person sicher erkannt werden. Deshalb werden zwei Begriffe getrennt:

- `Client`: HMAC-basierter, anonymisierter Schlüssel aus Netzwerk- und Browsermerkmalen
- `Visitor`: optionaler First-Party-Identifier des Beacons

Die UI darf einen geschätzten Client nicht als sicher identifizierte Person darstellen.

### 7.7 Request-Klassen

Jeder Request erhält:

```text
ActorType:
  Human
  Bot
  Internal
  Unknown

RequestType:
  Document
  Asset
  Api
  WebSocket
  HealthCheck
  Auth
  System
  Other

ClassificationConfidence:
  High
  Medium
  Low
```

### 7.8 Frameworkunabhängigkeit

Es gibt keine Nuxt-spezifische Hauptlogik. Frameworkpfade wie `/_nuxt/`, `/_next/`, `/assets/` oder `/wp-content/` sind lediglich vorinstallierte, editierbare Regeln. Primär gelten Header, Methode, MIME-/Pfadmerkmale und Site-Regeln.

## 8. Log-Ingestion

### 8.1 Pipeline

```text
Caddy JSON Log
  -> File Tailer
  -> Parser
  -> Normalizer
  -> Request Classifier
  -> Bounded Channel
  -> PostgreSQL Batch Writer
  -> Session/Pageview Processor
  -> Aggregate Processor
  -> Live Event Stream
```

### 8.2 File Tailer

Der Tailer speichert:

- Dateipfad
- Geräte-/Dateikennung, soweit verfügbar
- Byteposition
- letzte Eventzeit
- letzten Hash

Logrotation, Truncate und Containerneustart dürfen weder Daten duplizieren noch dauerhaft überspringen.

### 8.3 Deduplizierung

Falls Caddy keine Event-ID liefert, wird ein Fingerprint aus stabilen Feldern gebildet. Der Fingerprint dient nur zur kurzzeitigen Deduplizierung und ist kein fachlicher Primärschlüssel.

### 8.4 Batchimport

- begrenzter Channel mit Backpressure
- Batchgröße standardmäßig 1.000 Requests
- Flush spätestens nach 1 Sekunde
- Npgsql Binary Import für Rohrequests
- EF Core für normale Geschäftsobjekte
- fehlerhafte Zeilen werden separat protokolliert
- keine unbeschränkte In-Memory-Warteschlange

### 8.5 Aggregate

- minutennahe Stundenaggregate
- tägliche Verdichtung
- monatliche Verdichtung
- idempotente Jobs
- Rebuild für einen frei wählbaren Zeitraum
- eigener Jobstatus in der Systemseite

## 9. IP Intelligence, Bot-Erkennung und Sperren

### 9.1 IP-Normalisierung

- IPv4 und IPv6 werden normalisiert.
- Private, Loopback-, Link-Local-, Multicast-, reservierte und nicht öffentliche Bereiche werden lokal klassifiziert.
- Für nicht öffentliche Adressen erfolgt kein externer Lookup.

### 9.2 RIPEstat-Provider

Interface:

```csharp
public interface IIpIntelligenceProvider
{
    Task<IpIntelligenceResult> LookupAsync(
        IPAddress address,
        CancellationToken cancellationToken);
}
```

Der erste Provider bildet die aktuelle Main-Funktionalität mit RIPEstat nach:

- Network Info
- ASN
- Prefix
- AS Overview
- Holder
- Registry
- Source
- Fehlerstatus

HTTP-Timeouts sind kurz und blockieren keine Razor-Page-Anfrage. Nicht vorhandene Cachewerte werden über einen Hintergrundjob aktualisiert. Die Detailseite zeigt währenddessen einen neutralen Pending-Zustand.

### 9.3 Persistenter Cache

`IpIntelligenceCache` enthält mindestens:

- normalisierte IP
- Scope
- ASN
- Prefix
- Holder
- Registry
- Source
- FetchedAt
- ExpiresAt
- LastError
- FailureCount

### 9.4 Bot- und Risikobewertung

Die Bewertung wird als versionierte, deterministische Regel-Engine umgesetzt. Eingaben sind unter anderem:

- vorhandene Client-Klassifikation
- leerer oder verdächtiger User-Agent
- Requestrate
- Gleichförmigkeit der Abstände
- Anzahl unterschiedlicher Pfade
- typische Scannerpfade
- 404-Anteil
- 401-/403-Anteil
- Methoden
- Hostwechsel
- bekannte Bot-Signaturen

Ergebnis:

- Classification
- AutomationScore `0–100`
- RiskLevel `Low`, `Medium`, `High`, `Unknown`
- einzelne Gründe mit Gewicht
- Regelversion
- Zeitpunkt und betrachteter Zeitraum

### 9.5 Sperren

- manuelle Sperre und Entsperrung bleiben vorhanden
- Ablaufzeit und Grund sind Pflichtfelder
- jede Aktion erzeugt Audit- und Security-Event
- Caddy-Blockliste wird atomar aktualisiert
- Reload und Verify sind Pflicht
- fehlerhafte Aktualisierung wird zurückgerollt
- automatische Sperren sind standardmäßig aus
- eine spätere Auto-Block-Regel benötigt explizite Aktivierung, Mindestscore, Rate-Limit und maximale Sperrdauer

## 10. Oberfläche im AE01-Stil

## 10.1 Grundlayout

- feste linke Navigation auf Desktop
- kompakte mobile Navigation
- obere Command Bar
- klarer Seitenkontext
- Host- und Zeitraumfilter global verfügbar
- flache, dichte Tabellen
- Cards nur für wichtige Kennzahlen
- rechte Detailpaneele für schnelle Einsicht
- vollständige Detailseiten für tiefe Analyse
- System, Light und Dark Theme
- CSS-Variablen als Design Tokens
- keine visuelle Abhängigkeit vom AE01-Repository

### 10.2 Navigation

```text
Übersicht
Routen
Zugriff
Traffic
Besucher
Requests
Routenanalyse
Bots & Sicherheit
Fehler & Performance
Live-Log
DNS & DDNS
System
Administration
```

### 10.3 Dashboard

Kennzahlen:

- Pageviews
- fehlgeschlagene Navigationen
- Sessions
- Clients bzw. Besucher
- Requests gesamt
- Requests pro Pageview
- Traffic
- Fehlerquote
- p95-Latenz
- Bot-Anteil

Darunter:

- Pageviews und Requests als getrennte Zeitreihen
- Top-Seiten
- Top-API-Routen
- größte Assets
- langsamste Routen
- häufigste Fehler
- auffällige Clients
- System- und Ingestionstatus

### 10.4 Live-Aktualisierung

- Server-Sent Events für Live-Log und Status
- keine dauernde Vollseitenabfrage
- Filteränderungen über normale GET-Requests oder kleine Fetch-Requests
- URL enthält alle relevanten Filter
- Zurück-/Vorwärtsnavigation bleibt funktionsfähig

### 10.5 Diagramme

- serverseitig erzeugte Daten
- SVG oder kleines eigenes Canvas-Modul
- kein schweres Chart-Framework
- Tabellenalternative für Barrierefreiheit
- keine Animation, die große Datenmengen erneut rendert

## 11. Authentifizierung und Access Portal

### 11.1 Admin-Anmeldung

- ASP.NET-Core-Cookie-Authentifizierung
- persistente serverseitige Sessionreferenz
- Antiforgery-Tokens
- Origin-/Referer-Prüfung für sensible Formulare
- Rollenrichtlinien
- User-Agent-Bindung
- optionale IP-Bindung nur konfigurierbar, nicht Standard

### 11.2 TOTP

- TOTP pro Benutzer
- Recovery Codes
- erzwungene Einrichtung nur gemäß Konfiguration
- `CADDY_UI_REQUIRE_TOTP=false` bleibt gültig
- bei konfiguriertem öffentlichem Origin und deaktivierter Pflicht erscheint eine dauerhafte Sicherheitswarnung
- kein stiller Zwang oder automatisches Umschalten der Einstellung

### 11.3 Portal-Anmeldung

- eigenes Cookie-Schema
- eigener Antiforgery-Zweck
- getrennte TTL
- Access-Group-Isolation
- sichere Return-URL-Prüfung
- reservierter Auth-Pfad bleibt geschützt
- interne Identity-Header werden vor dem Upstream kontrolliert gesetzt

### 11.4 Legacy-Passwörter

Die bestehenden scrypt-Hashes müssen weiterhin geprüft werden können. Dafür wird ein isolierter Legacy-Password-Hasher verwendet. Nach erfolgreicher Anmeldung wird auf das neue Format rehashed. Die Abhängigkeit ist auf den Auth-Kompatibilitätsbereich begrenzt und wird mit festen Testvektoren geprüft.

## 12. Caddy-Konfigurationspipeline

Jede schreibende Änderung durchläuft:

1. Autorisierung
2. Eingabevalidierung
3. Erzeugung eines unveränderlichen Drafts
4. Rendering in temporäre Dateien
5. `caddy validate`
6. Diff gegen aktive Konfiguration
7. atomare Dateiersetzung
8. Caddy Reload
9. technische Verifikation
10. öffentliche bzw. Upstream-Healthprüfung, falls anwendbar
11. Commit der Revision
12. Audit Event

Bei Fehlern nach Schritt 7:

1. vorherigen Snapshot wiederherstellen
2. Caddy erneut laden
3. Rollback verifizieren
4. Operation als fehlgeschlagen markieren
5. vollständigen Fehler ohne Geheimnisse protokollieren

Golden-Master-Tests vergleichen während der Migration die Python- und .NET-Ausgabe für vorhandene Routenfälle.

## 13. Migration von SQLite zu PostgreSQL

### 13.1 Grundsatz

Die Migration verändert die SQLite-Datei nicht. Vor jedem Import wird eine Kopie und ein Hash erzeugt.

### 13.2 Ablauf

```text
caddy-ui migrate inspect
caddy-ui migrate dry-run
caddy-ui migrate execute
caddy-ui migrate verify
```

### 13.3 Importreihenfolge

1. Installation und Einstellungen
2. Benutzer und Rollen
3. TOTP und Recovery-Daten
4. Access Groups und Portalnutzer
5. Provider und DNS-Konfiguration
6. Routen und Revisionen
7. Security- und Audit-Events
8. Sperren
9. Statistische Aggregate
10. optionale Rohrequests innerhalb der konfigurierten Aufbewahrung
11. IP-Intelligence-Cache

### 13.4 Sessions

Bestehende Admin- und Portal-Sitzungen werden standardmäßig nicht übernommen. Nach der Umschaltung ist eine erneute Anmeldung erforderlich. Dies wird vor der Migration angezeigt und im Bericht protokolliert.

### 13.5 Verifikation

Der Bericht vergleicht:

- Anzahl je Entität
- eindeutige Schlüssel
- aktive Routen
- Hashes gerenderter Konfigurationen
- Rollen und Access-Group-Zuordnungen
- Blocklisten
- letzte Aggregate
- fehlende oder nicht importierbare Datensätze

## 14. Umsetzungsphasen

## Phase 0 – Bestand einfrieren und Verträge dokumentieren

Ziel: Der Neuaufbau darf keine aktuelle Main-Funktion verlieren.

Arbeiten:

- vollständige Funktionsmatrix des Python-Systems
- Inventar der SQLite-Tabellen und Migrationen
- Inventar der Environment-Variablen
- Inventar der Dateiformate und Volumes
- Golden-Master-Datensätze für Caddy-Rendering
- Regressionstests für LAN, Public Origin, TOTP false, Portal Login, Origin-null und CSRF
- IP-Intelligence-Testvektoren aus dem aktuellen Main-Stand
- Lastprofil der Log-Ingestion erfassen

Abnahme:

- jede bestehende Funktion ist einer Zielphase zugeordnet
- keine ungeklärte persistente Struktur
- kein Umschaltplan ohne Rückfallpfad

Rollback: nicht erforderlich, nur Dokumentation und Tests.

## Phase 1 – .NET-Grundgerüst und CI

Ziel: Buildbares, leeres Caddy-UI-2.0-System neben der bestehenden Anwendung.

Arbeiten:

- Solution und Projekte anlegen
- zentrale Build- und Analyzer-Einstellungen
- PostgreSQL-Testcontainer
- EF-Core-Migrationsmechanismus
- Health- und Readiness-Endpunkte
- Basis-Logging
- Razor-Layout und Design Tokens
- Docker-Build für Companion und Bundle
- Go-Module unverändert weiterbauen

Abnahme:

- `dotnet build` ohne Warnungen
- Unit-, Integrations- und Container-Smoke-Tests grün
- keine Beeinflussung des Python-Produktivpfads

Rollback: Branch bzw. neues Image verwerfen.

## Phase 2 – PostgreSQL-Schema und Migrationswerkzeug

Ziel: Vollständige Zielpersistenz und reproduzierbarer Import.

Arbeiten:

- Tabellen und Indizes
- Partitionierung
- Data-Protection-Persistenz
- SQLite-Reader
- Dry-Run und Verify
- Importbericht
- Backup vor Migration

Abnahme:

- wiederholbarer Import einer realistischen Datenkopie
- zweiter Import erzeugt keine Duplikate
- Abweichungen werden konkret gemeldet

Rollback: PostgreSQL-Schema löschen, SQLite bleibt unverändert.

## Phase 3 – Authentifizierung, Rollen und Portal

Ziel: Sicherheitskritische Funktionen zuerst vollständig ersetzen.

Arbeiten:

- Admin-Cookie-Schema
- Portal-Cookie-Schema
- Rollenrichtlinien
- TOTP und Recovery Codes
- Legacy-scrypt-Verifikation und Rehash
- Login-Rate-Limits und progressive Sperren
- CSRF- und Origin-Prüfung
- LAN-/Public-Origin-Verhalten

Abnahme:

- alle übernommenen Security-Regressionsfälle grün
- direkter LAN-Login funktioniert
- Portal-Login funktioniert
- `CADDY_UI_REQUIRE_TOTP=false` wird respektiert
- öffentliche Warnung wird angezeigt

Rollback: Python-Anwendung bleibt aktiver Auth-Endpunkt.

## Phase 4 – Log-Ingestion und echte Statistik

Ziel: Requestlast und fachliche Nutzung werden korrekt getrennt.

Arbeiten:

- File Tailer und Checkpoints
- Parser und Normalisierung
- Klassifikationsregelwerk
- Request-Batchimport
- Navigation, Pageview, Page Load und Session
- Stunden-/Tages-/Monatsaggregate
- Retention Jobs
- Vergleichslauf gegen aktuelle Statistik

Abnahme:

- ein Browseraufruf mit vielen Assets ergibt einen Pageview und alle tatsächlichen Requests
- keine doppelte Zählung nach Neustart oder Rotation
- definierte Genauigkeits- und Lasttests erfüllt
- UI kennzeichnet geschätzte Clients korrekt

Rollback: .NET-Ingestion stoppen, Checkpoint behalten oder löschen; Python bleibt aktiv.

## Phase 5 – IP Intelligence und Security Analytics

Ziel: Aktuelle Main-IP-Funktionen vollständig in die neue Architektur übernehmen.

Arbeiten:

- IP-Normalisierung
- RIPEstat-Provider
- persistenter Cache
- Hintergrundaktualisierung
- versionierte Bot-/Risikoengine
- Client-Detailseite
- manuelle Block-/Unblock-Pipeline
- Security- und Audit-Historie

Abnahme:

- private IPs lösen keinen externen Request aus
- öffentliche IPs zeigen ASN, Prefix, Holder und Registry
- Fehler blockieren keine Seite
- Score und Gründe sind reproduzierbar
- Sperre kann sicher angewendet und zurückgerollt werden

Rollback: keine automatische Sperre; Python bleibt für aktive Blockänderungen zuständig, bis Phase freigegeben ist.

## Phase 6 – Read-only Razor-Pages-Oberfläche

Ziel: Vollständige neue Anzeige ohne schreibende Caddy-Änderungen.

Arbeiten:

- Übersicht
- Traffic
- Besucher und Clients
- Requests
- Routenanalyse
- Bots und Sicherheit
- Fehler und Performance
- Live-Log
- Systemstatus
- globale Filter
- responsive AE01-nahe Gestaltung

Abnahme:

- alle Kerndaten ohne SPA bedienbar
- keine Vollseiten-Neuladung für Live-Log nötig
- mobile Nutzung möglich
- Dark, Light und System Theme

Rollback: neue UI abschalten, Python-UI weiterverwenden.

## Phase 7 – Routen, Access und Caddy-Schreibpfad

Ziel: Vollständige Verwaltung von Routen und geschützten Anwendungen.

Arbeiten:

- Routen-CRUD
- Upstreams und Healthchecks
- Access Groups
- Preview und Diff
- Validate, Apply, Reload, Verify und Rollback
- Revisionen
- Import und Export
- Custom Routes

Abnahme:

- Golden-Master-Ausgaben stimmen fachlich überein
- Fehlerfall stellt vorherige Konfiguration wieder her
- Audit-Historie vollständig
- keine Secret-Ausgabe in Diff oder Logs

Rollback: Feature Flag setzt alle Schreibaktionen zurück auf Python oder deaktiviert sie.

## Phase 8 – DNS, DDNS, Benachrichtigungen und Systemfunktionen

Ziel: Restliche Produktfunktionen übernehmen.

Arbeiten:

- Netcup DNS
- DDNS-Jobs
- E-Mail
- Webhook
- Discord
- Telegram
- Backups
- Diagnoseexport
- Jobübersicht
- Public- und Upstream-Health

Abnahme:

- Providerfehler sind sichtbar und retrybar
- Secrets bleiben geschützt
- Schedulerjobs sind idempotent

Rollback: einzelne Integrationen per Feature Flag deaktivieren.

## Phase 9 – Shadow-Betrieb und Umschaltung

Ziel: kontrollierte Produktionsmigration.

Arbeiten:

- .NET parallel auf internem Testport
- gleiche Logs read-only verarbeiten
- Statistiken vergleichen
- Datenmigration im Dry-Run
- Wartungsfenster
- finaler SQLite-Import
- Umschaltung von `8098` und `8099`
- Verifikation aller Login-, Route-, DNS- und Statistikpfade

Abnahme:

- definierte Abweichungstoleranz der Statistiken eingehalten
- LAN und Public Origin funktionieren
- Portal funktioniert
- aktive Routen unverändert
- Blockliste unverändert
- Backups vorhanden

Rollback:

- .NET stoppen
- Python-Container mit unveränderter SQLite-Datei starten
- vorherige Caddy-Dateien und Blockliste wiederherstellen
- Ports zurückschalten

## Phase 10 – Entfernung der Python-Laufzeit

Ziel: Python erst nach stabiler 2.0-Produktion entfernen.

Voraussetzungen:

- mindestens zwei stabile Releases nach Umschaltung
- keine offenen Blocker
- Migration mehrfach erfolgreich getestet
- Rollback-Dokumentation geprüft
- alle Python-Funktionen in der Funktionsmatrix abgehakt

Arbeiten:

- Python-Anwendung entfernen
- Legacy-Hotfixmodule entfernen
- alte SQLite-Schreibpfade entfernen
- Images verkleinern
- Dokumentation und Compose endgültig umstellen

Abnahme:

- kein Python-Runtime-Paket im finalen UI-Image
- keine ungenutzten Migrationspfade im normalen Startup
- Version `2.0.0` veröffentlicht

## 15. Parallele Arbeitsbereiche

Folgende Bereiche können nach Phase 0 teilweise parallel bearbeitet werden:

1. Solution, Docker und CI
2. PostgreSQL-Schema und Migration
3. Razor-Designsystem und Layout
4. Log-Ingestion und Statistik
5. IP Intelligence und Security Analytics
6. Authentifizierung und Portal
7. Caddy-Rendering-Golden-Master-Tests

Nicht parallel freigeben:

- produktiver Auth-Wechsel vor vollständigen Security-Tests
- Caddy-Schreibpfad vor Validate-/Rollback-Tests
- automatische Sperren vor manueller Sperrpipeline
- Python-Entfernung vor abgeschlossener Produktionsstabilisierung

## 16. Teststrategie

### 16.1 Unit Tests

- Request-Klassifikation
- Pageview- und Sessionlogik
- Bot-Score
- IP-Scope
- Route-Rendering
- Berechtigungen
- Retentionberechnung

### 16.2 Integrationstests

- PostgreSQL
- Partitionen und Migrationen
- RIPEstat mit Stubserver
- Caddy Admin API mit Stubserver
- Dateirotation
- Login- und Portal-Cookies
- Antiforgery
- Data Protection

### 16.3 Golden-Master-Tests

- bestehende Caddyfiles
- Route-Revisionsdaten
- Access-Portal-Konfiguration
- Blockliste
- Exportformate

### 16.4 Acceptance Tests

- LAN-Adresse auf Port 8098
- öffentlicher exakter Origin
- TOTP aus und an
- Portal-Login
- Logout
- Origin-null-Sonderfall
- Favicon-/Asset-Anfragen invalidieren keinen Login-CSRF-Zustand
- Pageview mit vielen Assets
- API-only-Traffic
- Bot-Scanner
- IPv4 und IPv6
- Logrotation
- Caddy-Apply mit erfolgreichem Rollback

### 16.5 Lasttests

Mindestens:

- 100 Requests pro Sekunde über 15 Minuten
- Burst von 1.000 Requests pro Sekunde
- 10 Millionen bestehende Rohrequests
- gleichzeitige Live-Log-Clients
- langsamer PostgreSQL-Writer mit Backpressure

Es dürfen keine unbegrenzten Queues oder linearen Volltabellenscans im normalen Dashboard entstehen.

## 17. CI/CD

Pull Requests:

- Textformat und Formatierung
- `dotnet restore`
- `dotnet build`
- Unit- und Integrationstests
- PostgreSQL-Migrationstest
- Go Format und Go Tests
- Companion- und Bundle-Imagebuild
- Container-Smoke-Test
- Secret- und Migrationsprüfung

Main:

- alle PR-Prüfungen
- Versionierung
- Images erst nach vollständigem Erfolg veröffentlichen
- Migrationsartefakt und Release Notes
- Update-PR für `Juloc/docker`

Die 2.0-Migration darf nicht automatisch beim ersten Containerstart ohne explizite Freigabe destruktiv ausgeführt werden.

## 18. Neue Konfiguration

Mindestens folgende neue Variablen werden eingeführt:

```env
CADDY_UI_CONNECTION_STRING=Host=postgres;Port=5432;Database=caddy_ui;Username=caddy_ui;Password=...
CADDY_UI_AUTO_MIGRATE=false
CADDY_UI_IMPORT_SQLITE_PATH=/var/lib/caddy-ui/caddy-ui.db
CADDY_UI_RAW_REQUEST_RETENTION_DAYS=30
CADDY_UI_PAGEVIEW_RETENTION_DAYS=180
CADDY_UI_SESSION_IDLE_MINUTES=30
CADDY_UI_INGEST_BATCH_SIZE=1000
CADDY_UI_INGEST_FLUSH_MS=1000
CADDY_UI_IP_LOOKUP_ENABLED=true
CADDY_UI_IP_LOOKUP_SUCCESS_TTL_SECONDS=86400
CADDY_UI_IP_LOOKUP_ERROR_TTL_SECONDS=600
CADDY_UI_AUTO_BLOCK_ENABLED=false
```

Bestehende Variablen werden, soweit fachlich passend, übernommen oder mit einer klaren Deprecation-Warnung unterstützt.

## 19. Nicht Teil von Version 2.0

- Docker-Containerverwaltung
- Kubernetes-Verwaltung
- allgemeines Serverterminal
- automatische KI-Entscheidung über IP-Sperren
- Identifikation realer Personen hinter IP-Adressen
- zwingende SPA- oder Blazor-Migration
- TimescaleDB oder andere erforderliche PostgreSQL-Erweiterungen
- externe Analytics-SaaS-Abhängigkeit

## 20. Definition of Done für 2.0

Version 2.0 ist fertig, wenn:

- alle Funktionen der Phase-0-Matrix übernommen oder bewusst entfernt und dokumentiert sind
- PostgreSQL produktiv verwendet wird
- SQLite nur noch Importquelle ist
- Razor Pages die vollständige Oberfläche bereitstellt
- Requests und Pageviews getrennt und korrekt dargestellt werden
- IP Intelligence und Bot-Bewertung aus Main vorhanden sind
- LAN-, Public-Origin-, TOTP- und Portal-Verhalten getestet sind
- Caddy-Änderungen validiert und rollbackfähig sind
- Migration und Rückfallweg praktisch getestet wurden
- keine Python-Laufzeit mehr im finalen UI-Container benötigt wird
- Dokumentation, Compose und Releaseprozess aktualisiert sind
