# Shadow-Deployment-Checkliste

Diese Checkliste ist während des echten Phase-9-Shadow-Laufs auszufüllen. Eine leere oder nur lokal angenommene Prüfung reicht nicht für die Umschaltung.

## Vor dem Start

- [ ] produktiver Python-/SQLite-Stack läuft unverändert
- [ ] automatische Releases sind pausiert
- [ ] aktuelles Backup der Legacy-SQLite-Datei vorhanden
- [ ] absoluter read-only Logpfad bekannt
- [ ] absoluter read-only SQLite-Pfad bekannt
- [ ] Shadow-Port ist frei und nur an Loopback gebunden
- [ ] getrennte zufällige PostgreSQL- und Admin-Passwörter gesetzt
- [ ] `scripts/shadow-preflight.sh` erfolgreich

## Start und Basisprüfung

- [ ] PostgreSQL wird healthy
- [ ] Migrationscontainer endet erfolgreich
- [ ] .NET-Container wird healthy
- [ ] `/health/live` erfolgreich
- [ ] `/health/ready` erfolgreich
- [ ] Admin-Login über SSH-Tunnel erfolgreich
- [ ] Portal-Port 8099 ist nicht vom Host erreichbar
- [ ] keine produktive Caddy-Route zeigt auf den Shadow-Container

## Sicherheitsgrenzen

- [ ] `Cutover:Enabled=false`
- [ ] Routing-Modus `disabled`
- [ ] DNS-Modus `disabled`
- [ ] Operations-Worker deaktiviert
- [ ] Blocklist-Modus `disabled`
- [ ] IP Intelligence deaktiviert
- [ ] Risikoworker deaktiviert
- [ ] kein Docker-Socket eingebunden
- [ ] Logs und SQLite ausschließlich read-only eingebunden
- [ ] keine produktive Caddy-, DNS-, Zertifikat- oder Blocklist-Datei verändert

## Beobachtungszeitraum

- [ ] mindestens 24 Stunden oder konfigurierte Mindestdauer erreicht
- [ ] letzter Request höchstens 15 Minuten alt
- [ ] Checkpoint steigt kontinuierlich
- [ ] Neustart verarbeitet keine Ereignisse doppelt
- [ ] Logrotation verarbeitet Folgezeilen korrekt
- [ ] Speicherverbrauch bleibt stabil
- [ ] PostgreSQL-Wachstum entspricht dem erwarteten Traffic
- [ ] ein HTML-Aufruf plus Assets bleibt ein Pageview und mehrere Requests

## Statistikvergleich

- [ ] geschlossenes UTC-Zeitfenster gewählt
- [ ] Legacy-Snapshot enthält `capturedAt`, `windowStart`, `windowEnd`, `requests`, `pageViews`, `sessions`, `clients`, `errors`
- [ ] Snapshot in `/state/legacy-statistics.json` bereitgestellt
- [ ] Requests innerhalb der Toleranz
- [ ] Pageviews innerhalb der Toleranz
- [ ] Sessions innerhalb der Toleranz
- [ ] Clients innerhalb der Toleranz
- [ ] HTTP-5xx innerhalb der Toleranz
- [ ] Vergleichsmanifest gespeichert
- [ ] Readiness-Manifest gespeichert

## Backup und Rückfall

- [ ] aktuelles PostgreSQL-Backup erzeugt
- [ ] Backup-Digest dokumentiert
- [ ] Test-Wiederherstellung erfolgreich
- [ ] Legacy-SQLite-Backup lesbar
- [ ] bisherige Compose-Datei und Imageversion dokumentiert
- [ ] Rückfall auf bisherigen Container praktisch getestet

## Freigabe

- [ ] keine blockierende Readiness-Prüfung offen
- [ ] Warnungen bewertet und dokumentiert
- [ ] Wartungsfenster festgelegt
- [ ] Abnahmekriterien festgelegt
- [ ] Rückfallkriterien festgelegt
- [ ] `Cutover:Enabled=true` wird erst unmittelbar im Wartungsfenster gesetzt

Phase 10 beginnt erst nach zwei stabilen .NET-Releases und einem erfolgreich dokumentierten Rückfalltest.
