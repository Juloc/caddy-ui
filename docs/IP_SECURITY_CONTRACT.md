# IP-, Bot- und Blockierungsvertrag

Status: verbindlich für Caddy UI 2.0  
Eingeführt: Phase 5

## IP-Normalisierung

IPv4, IPv6 und IPv4-mapped IPv6 werden auf eine kanonische Adresse normalisiert. Private, Loopback-, Link-Local-, Multicast-, Dokumentations-, Shared-, Benchmark-, reservierte und unspezifizierte Bereiche werden lokal klassifiziert.

Nur global öffentliche Adressen dürfen an einen externen Intelligence-Provider gesendet werden. Private oder reservierte Adressen erzeugen niemals einen RIPEstat-Aufruf.

## RIPEstat

Öffentliche IPs werden im Hintergrund über RIPEstat angereichert. Verwendet werden Network Info für Prefix und originierende ASN sowie AS Overview für Holder und Registry. Die UI wartet nicht auf den Provider.

- erfolgreiche Ergebnisse werden standardmäßig 24 Stunden gecacht
- Fehler werden standardmäßig 10 Minuten gecacht
- Fehler verwenden exponentielles Backoff
- Lookups laufen ausschließlich im Hintergrund
- Providerfehler verändern keine Requests, Clients oder Blockregeln
- der externe Lookup ist standardmäßig deaktiviert

## Bot- und Risikobewertung

Die Bewertung ist deterministisch und versioniert. `risk-v1` verwendet unter anderem:

- bekannte Bot- und Automation-Signaturen
- fehlenden User-Agent
- Requestrate
- gleichförmige Requestintervalle
- Scanner- und Exploit-Pfade
- Anzahl unterschiedlicher Pfade und Hosts
- 404-, 401- und 403-Anteile
- ungewöhnliche HTTP-Methoden

Jede Gewichtung wird als eigener Reason-Datensatz mit Evidenz gespeichert. Gleiche Eingabedaten und dieselbe Engine-Version ergeben dieselbe Bewertung. Eine Bewertung ist keine sichere Identifikation einer Person.

## Compact security evidence

The lightweight ingestion path supplies risk scoring and IP discovery from compact per-client minute buckets instead of raw request history. Buckets retain counts, error ratios, timing moments, scanner counts, a bounded set of path hashes and host names, and the latest technical address/user-agent. Path samples are capped at 64 and host samples at 8, above the engine's respective detection thresholds of 50 and 6. The configured risk/discovery windows remain authoritative; evidence expires after those windows plus the maintenance interval. DDNS, request-guard enforcement, manual blocks and risk scoring do not depend on any open UI page.

## Manuelle Sperren

Jede Sperre benötigt:

- eine exakte IPv4- oder IPv6-Adresse
- einen Grund
- eine Ablaufzeit
- einen authentifizierten Editor oder Administrator

Der aktuelle Caddy-Guard unterstützt exakte Adressen. Netzsperren werden deshalb nicht stillschweigend in ein inkompatibles Format geschrieben.

Blockieren und Entsperren erzeugen:

- eine Blockregel
- einen History-Eintrag
- ein Security Event
- ein Audit Event mit Correlation-ID

Die Blocklist wird in eine temporäre Datei geschrieben, auf Platte synchronisiert, atomar ersetzt und anschließend erneut geparst. Bei Datenbank- oder Dateifehlern wird die vorherige Datei wiederhergestellt.

## Betriebsmodi

- `disabled`: nur Datenbank- und Auditpfad, keine Dateiänderung
- `shadow`: separate Preview-Blocklist, keine produktive Sperrwirkung
- `active`: schreibt die konfigurierte Caddy-Guard-Blocklist

The default is `disabled`; production Compose enables `active` enforcement through the Caddy request guard.
