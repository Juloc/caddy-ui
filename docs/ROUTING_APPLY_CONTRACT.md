# Caddy UI 2.0 – Routing- und Apply-Vertrag

Status: verbindlich ab Phase 7

## Ziel

Routen werden fachlich in PostgreSQL verwaltet. Die laufende Caddy-Konfiguration wird niemals direkt durch ein CRUD-Formular verändert. Jede Änderung durchläuft eine unveränderliche Revision mit Preview, Diff, Validierung und auditierbarem Apply.

## Routenmodell

Jede Route besitzt:

- genau eine verwaltete Domain
- eine Subdomain oder den Domain-Root
- einen normalisierten Host
- einen Pfadpräfix
- einen Routentyp
- eine eindeutige Reihenfolge
- einen Zertifikatsmodus
- optional eine Zugriffsgruppe
- einen aktiv/deaktiviert-Status

Unterstützte Typen:

- Reverse Proxy
- Redirect
- statische Antwort
- benutzerdefinierte Caddyfile-Direktiven, standardmäßig deaktiviert

Eine aktivierte Route bedeutet nur, dass sie in die nächste generierte Revision aufgenommen wird. Sie verändert Caddy nicht sofort.

## Validierung

- Host und Subdomain werden aus der verwalteten Domain abgeleitet.
- Pfade beginnen mit `/` und enthalten keine Traversal- oder Zeilenumbruchsequenzen.
- Upstreams sind `host:port` oder HTTP(S)-URLs.
- Ports liegen zwischen 1 und 65535.
- Redirects sind lokale Pfade oder HTTP(S)-URLs.
- generierte Werte dürfen keine Caddyfile-Block- oder Zeileninjektion enthalten.
- pro Host und Pfadpräfix darf nur eine aktivierte Route existieren.
- Secrets sind in Routen-JSON und generierten Diffs verboten.

## Import und Export

Das portable Format verwendet das Schema `caddy-ui-routes-v1`.

Ein Export enthält:

- Routenname
- Domain- und Subdomainname
- Routentyp und Aktivstatus
- Reihenfolge und Zertifikatsmodus
- optionalen Namen der Zugriffsgruppe
- typisierte Routenkonfiguration

Nicht exportiert werden:

- Portal- oder Admin-Kennwörter
- Provider-Secrets
- Sitzungen und Recovery-Codes
- Caddy-Snapshots und Apply-Historie
- Data-Protection-Schlüssel

Importregeln:

- ein Dokument enthält zwischen 1 und 500 Routen
- Schema, Domain und optionale Zugriffsgruppe werden vollständig geprüft
- Domains und Gruppen müssen auf dem Zielsystem existieren und aktiviert sein
- sämtliche Routenvalidierungen werden erneut ausgeführt
- Custom Routes werden abgelehnt, solange das Feature deaktiviert ist
- doppelte aktive Ziele innerhalb des Imports werden abgelehnt
- Konflikte mit vorhandenen aktiven Zielen werden abgelehnt
- alle Einfügungen erfolgen in einer serialisierbaren PostgreSQL-Transaktion
- bei einem einzigen Fehler wird keine Route aus dem Dokument gespeichert
- ein Import erzeugt nur Entwürfe; Caddy wird nicht validiert, geschrieben oder neu geladen
- der Import wird ohne Kennwörter oder Secrets auditiert

## Zugriffsgruppen

Eine Route kann optional über das interne Portal geschützt werden. Der Generator schreibt dafür `forward_auth` zum Portal auf Port 8099 und übergibt ausschließlich kontrollierte Identitätsheader.

- Gruppen und Portalzugänge werden getrennt verwaltet.
- Kennwörter werden mit dem bestehenden Passwortdienst gehasht.
- gespeicherte Kennwörter werden nie wieder angezeigt.
- deaktivierte Gruppen oder Zugänge erlauben keine neue Anmeldung.
- deaktivierte Gruppen machen bestehende Portal-Sitzungen für die Gruppe sofort unwirksam.

## Revision

Eine Revision enthält:

- vollständigen generierten Caddyfile-Fragmentinhalt
- SHA-256-Digest
- Manifest mit Routen und Zertifikatsanforderungen
- Ersteller und Grund
- Erstellzeitpunkt
- Applied-Status

Revisionen werden nicht nachträglich verändert.

## Schreibmodi

### disabled

- Standardwert
- Preview und Diff sind möglich
- kein Dateischreibzugriff
- kein Caddy-Aufruf

### shadow

- Kandidat wird mit Caddy validiert
- atomischer Schreibzugriff nur auf die separate Shadow-Datei
- kein Reload der produktiven Caddy-Konfiguration

### active

Nur nach expliziter Konfiguration:

1. Root-Caddyfile und Importvertrag prüfen
2. bisherigen Fragmentstand als Snapshot sichern
3. Kandidat erzeugen
4. Kandidat mit Caddy validieren
5. Fragment atomisch ersetzen
6. vollständige Root-Konfiguration validieren
7. Caddy reload ausführen
8. aktive Konfiguration erneut validieren
9. Operation, Schritte und Audit abschließen

Apply und Rollback sind pro Prozess serialisiert. Parallele Schreiboperationen sind nicht erlaubt.

## Rollback

Bei einem Fehler nach dem Dateiaustausch:

- vorherigen Inhalt wiederherstellen oder neu erzeugte Datei entfernen
- Caddy erneut laden
- Rollback-Schritt und mögliches Rollback-Problem protokollieren
- Operation als fehlgeschlagen abschließen

Ein manueller Rollback stellt den Snapshot vor dem letzten erfolgreichen Apply wieder her.

## Zertifikate

Der Domainvertrag bleibt maßgeblich:

- neue Domains verwenden standardmäßig Wildcard
- Routen verwenden standardmäßig `inherit`
- kein stiller Rückfall auf Einzelzertifikate

Phase 7 erzeugt noch keinen DNS-/Wildcard-Renderer. Deshalb gilt:

- Shadow-Preview und Shadow-Validierung bleiben möglich.
- produktiver Active-Apply wird bei `wildcard` und konservativ auch bei `inherit` blockiert.
- Active-Apply ist in Phase 7 nur für explizite Einzelzertifikate zulässig.
- Phase 8 ergänzt den Provider- und Wildcard-Renderer.

## Legacy-Routen

Importierte Legacy-Routen ohne `domain_id` dürfen nicht in den produktiven .NET-Schreibpfad übernommen werden. Vor Freigabe von Active-Apply müssen sie kontrolliert einer verwalteten Domain zugeordnet und im Shadow-Vergleich geprüft werden.

## Audit

Folgende Aktionen benötigen Auditdaten und Correlation-ID:

- Route anlegen, ändern und löschen
- Routenimport
- Revision erzeugen
- Apply starten und abschließen
- jeder Apply-Schritt
- automatischer oder manueller Rollback
- Zugriffsgruppe und Portalzugang ändern

Auditdaten enthalten keine Kennwörter, Provider-Secrets oder aufgelösten Umgebungsvariablen.

## Produktionsfreigabe

Active-Apply darf erst freigegeben werden, wenn:

1. alle Legacy-Routen einer Domain zugeordnet sind.
2. der Root-Caddyfile-Import geprüft ist.
3. Shadow-Ausgabe gegen den bestehenden Python-Golden-Master verglichen wurde.
4. Validierungs-, Reload- und Rollbacktest mit einer Test-Route erfolgreich waren.
5. der Wildcard-Renderer für Wildcard-/Inherit-Routen verfügbar ist.
6. Dateirechte, Persistenz und Backup geprüft wurden.
