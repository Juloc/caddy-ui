# Analytics-Vertrag

Status: verbindlich für Caddy UI 2.0  
Eingeführt: Phase 4

## Getrennte Größen

Caddy UI vermischt fachliche Nutzung nicht mit HTTP-Last.

- **Request:** jeder von Caddy verarbeitete HTTP-Request, einschließlich HTML, JavaScript, CSS, Bilder, Fonts, API, WebSocket, Healthchecks, Redirects, Bots und Fehler.
- **Navigation:** ein mutmaßlicher Browseraufruf eines Dokuments. Redirects und fehlgeschlagene Dokumentaufrufe können Navigationen sein.
- **Pageview:** eine erfolgreiche Dokumentnavigation. HTTP-Redirects sind keine eigenen Pageviews; `304 Not Modified` zählt als erfolgreiche Dokumentnavigation.
- **Page Load:** ein Pageview zusammen mit den kurz danach beobachteten Asset- und API-Requests desselben geschätzten Clients.
- **Session:** zusammenhängende fachliche Nutzung desselben Clients und Hosts. Standardmäßig endet sie nach 30 Minuten Inaktivität.
- **Client/Besucher:** bevorzugt über eine explizite First-Party-ID, andernfalls als pseudonymisierte Schätzung aus Caddys `client_ip` und User-Agent.

Ein Aufruf von Mealie mit einem HTML-Dokument und 100 Nuxt-Dateien ergibt daher:

- 101 Requests
- 1 Navigation
- 1 Pageview
- 1 Page Load
- 100 Asset-Requests

## Zweidimensionale Klassifikation

Akteur und Ressourcentyp sind getrennte Dimensionen.

Akteur:

- `human`
- `bot`
- `internal`
- `unknown`

Ressourcentyp:

- `document`
- `asset`
- `api`
- `websocket`
- `healthcheck`
- `auth`
- `system`
- `other`

Ein Eintrag `human + asset` bleibt ein technischer Asset-Request und wird niemals als Pageview gezählt.

## Dokumenterkennung

Die Klassifikation verwendet gewichtete Evidenz:

1. `Sec-Fetch-Dest: document`
2. Antworttyp `text/html`
3. `Accept: text/html`
4. Methode `GET` oder `HEAD`
5. kein bekannter Asset-, API-, Healthcheck-, Auth- oder Systempfad
6. kein Bot- oder interner Akteur

Asset-Präfixe wie `/_nuxt/`, `/_next/`, `/assets/` und `/static/` werden separat klassifiziert. Gehashte Assetdateien erscheinen nicht als Top-Seiten.

## Datenschutz und Secrets

- Rohlogs werden vor dem Speichern bereinigt.
- Authorization-, Cookie-, Secret-, Token-, Passwort-, API-Key- und Signaturwerte werden entfernt.
- Empfindliche Queryparameter werden als `[redacted]` gespeichert.
- Nicht parsebare Zeilen werden nur als SHA-256-Fingerabdruck und Länge protokolliert.
- Clientkennungen werden mit einem zufälligen, über ASP.NET Core Data Protection geschützten HMAC-Schlüssel pseudonymisiert.
- Bei rein proxybasierter Erkennung wird der Client ausdrücklich als geschätzt markiert.
- Forwarded Header werden nicht blind für die Clientidentität verwendet; maßgeblich ist Caddys aufgelöstes `client_ip`.

## Ingestion und Idempotenz

- Dateien werden ab einem persistenten Byte-Checkpoint gelesen.
- Dateiersetzung und Rotation werden über eine Quellidentität erkannt.
- Nur vollständig abgeschlossene Zeilen werden verarbeitet.
- Request und Checkpoint werden in derselben PostgreSQL-Transaktion geschrieben.
- Ein Neustart oder erneutes Lesen derselben Quellposition erzeugt keine doppelten Requests, Pageviews oder Aggregate.
- Es gibt keine unbegrenzte In-Memory-Queue.
- Requests, Clients, Session-Zähler, Page-Load-Zähler und Aggregate werden innerhalb eines Ingestion-Batches zusammengefasst, um PostgreSQL-Roundtrips zu begrenzen.
- Die Standard-Batchgröße beträgt 500 Requests.
- Solange ein Backlog vorhanden ist, liegt zwischen zwei Worker-Durchläufen standardmäßig eine kooperative Pause von 100 ms. Ohne Backlog gilt das normale Polling-Intervall von 1000 ms.
- Batchgröße und Backlog-Pause sind über `CADDY_UI_INGEST_BATCH_SIZE` und `CADDY_UI_INGEST_BACKLOG_DELAY_MS` konfigurierbar.

## Aggregate und Aufbewahrung

Bei der Ingestion werden Stunden-, Tages-, Monats- und Routenaggregate aktualisiert. Rohrequests liegen in monatlichen PostgreSQL-Partitionen.

Standardwerte:

- Rohrequests: 30 Tage
- Navigationen und Pageviews: 180 Tage
- Stundenaggregate: 90 Tage
- Tagesaggregate: 730 Tage
- Monatsaggregate: unbegrenzt
- Session-Timeout: 30 Minuten

Der Wartungsjob schließt inaktive Sessions und Page Loads, entfernt abgelaufene Daten, löscht vollständig abgelaufene Requestpartitionen und legt die aktuelle sowie die nächste Monatspartition an. Vollständig abgelaufene Monats-Partitionen werden vor zeilenweisen Fallback-Bereinigungen entfernt.

## SPA-Grenze

Proxylogs erkennen vollständige Dokumentnavigationen. Rein clientseitige SPA-Routenwechsel erzeugen keinen neuen HTTP-Dokumentrequest und können deshalb nur über ein optionales First-Party-Pageview-Beacon exakt gezählt werden. Ohne Beacon werden sie nicht erfunden.
