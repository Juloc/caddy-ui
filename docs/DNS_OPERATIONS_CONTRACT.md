# Caddy UI 2.0 – DNS-, DDNS- und Betriebsvertrag

Status: verbindlich ab Phase 8

## 1. Grundprinzipien

- Jede verwaltete Route gehört genau einer verwalteten Domain.
- Jede Domain kann genau einem aktivierten DNS-Provider zugeordnet werden.
- Neue Domains verwenden standardmäßig Wildcard-Zertifikate.
- Neue Routen verwenden standardmäßig `inherit` und übernehmen damit den Zertifikatsmodus ihrer Domain.
- DNS-Management, direkte Provider-API, DDNS und Caddy-DNS-01 sind getrennte Fähigkeiten.
- Kein Provider-Secret wird als Klartext in PostgreSQL gespeichert.
- Kein schreibender Worker ist standardmäßig aktiv.

## 2. Provider-Katalog und Laufzeitunterstützung

Der Management-Katalog enthält:

- Netcup
- Cloudflare
- Amazon Route 53
- DigitalOcean
- Hetzner DNS
- IONOS
- OVHcloud
- Porkbun
- Namecheap
- Gandi
- deSEC
- Google Cloud DNS
- Azure DNS
- Vultr
- Linode/Akamai
- GoDaddy
- DuckDNS
- RFC 2136

Der Katalog beschreibt Felder, Secret-Referenzen und fachliche Fähigkeiten. Ein Katalogeintrag bedeutet nicht automatisch, dass im aktuellen Build ein direkter API-Adapter oder ein Caddy-DNS-Modul vorhanden ist.

Direkte API-Adapter in Phase 8:

| Provider | Verbindungstest | Record-Update | DDNS |
| --- | --- | --- | --- |
| Netcup | ja | ja | ja |
| Cloudflare | ja | ja | ja |
| DigitalOcean | ja | ja | ja |
| Hetzner DNS | ja | ja | ja |
| IONOS | ja | ja | ja |
| Gandi | ja | ja | ja |
| deSEC | ja | ja | ja |
| DuckDNS | ja | A/AAAA | ja |

Alle übrigen Katalogprovider bleiben im Management auswählbar. Schreibaktionen werden mit einer klaren Fehlermeldung blockiert, solange kein geprüfter direkter Adapter vorhanden ist.

## 3. Secret-Referenzen

Unterstützt werden:

```text
ENVIRONMENT_VARIABLE
secret://env/ENVIRONMENT_VARIABLE
secret://file/absolute/path
```

Regeln:

- Die Datenbank speichert nur die Referenz.
- Direkte API-Adapter lösen Referenzen erst für den einzelnen Aufruf auf.
- Diagnoseexporte enthalten nur Secret-Feldnamen, keine Referenzen oder Werte.
- Caddy-DNS-01 kann nur Umgebungsvariablen verwenden, weil Caddy die Werte selbst beim Laden auflösen muss.
- Datei-Secrets bleiben für direkte API-Aufrufe zulässig, können aber nicht in einen Caddyfile-DNS-01-Block übernommen werden.

## 4. DNS-Schreibmodi

### disabled

Standardwert.

- DNS-Entwürfe können angelegt und geprüft werden.
- Provider-Verbindungstests sind möglich.
- Kein DNS-Record wird geschrieben.
- DDNS meldet einen blockierten Schreibpfad.

### shadow

- Mutation wird vollständig validiert.
- Es findet kein externer DNS-Schreibzugriff statt.
- Die UI zeigt präzise, welcher Record geschrieben würde.

### active

Nur nach expliziter Konfiguration.

- Provider und Domain müssen aktiviert und fest zugeordnet sein.
- Der direkte Adapter muss den Provider unterstützen.
- Secret-Referenzen müssen auflösbar sein.
- Provider-Antwort und Fehlerstatus werden gespeichert.
- Fehler erzeugen dauerhafte In-App-Benachrichtigungen und optional externe Meldungen.

Konfiguration:

```text
Operations:DnsWriteMode=disabled|shadow|active
CADDY_UI_DNS_WRITE_MODE=disabled|shadow|active
```

## 5. Wildcard-Zertifikate

Der effektive Modus wird pro Route berechnet:

```text
route=individual -> Einzelzertifikat
route=wildcard   -> DNS-01/Wildcard
route=inherit    -> Domainstandard
```

Der Compiler erzeugt einen hostbezogenen TLS-/DNS-01-Block nur, wenn:

1. Wildcard effektiv erforderlich ist.
2. die Domain einen aktivierten Provider besitzt.
3. das passende Caddy-DNS-Modul als installiert konfiguriert ist.
4. ein geprüfter Renderer vorhanden ist.
5. alle benötigten Caddy-Secret-Referenzen Umgebungsvariablen sind.

Phase 8 aktiviert den geprüften Renderer für das im Bundle enthaltene Netcup-Modul:

```caddyfile
tls {
    dns netcup {
        customer_number "123456"
        api_key "{env.NETCUP_API_KEY}"
        api_password "{env.NETCUP_API_PASSWORD}"
    }
}
```

Fehlt eine Voraussetzung, bleiben Preview und Diff verfügbar. Ein aktiver Apply bleibt blockiert. Es gibt keinen stillen Rückfall auf Einzelzertifikate.

## 6. Verwaltete DNS-Records

Unterstützte Recordtypen:

```text
A AAAA CNAME TXT MX CAA SRV
```

- TTL liegt zwischen 30 und 86400 Sekunden.
- Domain und Provider müssen aktiviert und einander zugeordnet sein.
- Ein Record wird zuerst in PostgreSQL angelegt.
- Synchronisierung ist eine separate, sichtbare Aktion.
- Status, Zeitpunkt und letzter Fehler werden dauerhaft gespeichert.
- Deaktivierte Records werden nicht manuell synchronisiert.

## 7. DDNS

DDNS unterstützt `A` und `AAAA`.

Adressquellen:

- `public`: Erkennung über mehrere konfigurierbare HTTPS-Dienste
- `static`: explizit konfigurierte IP-Adresse

Regeln:

- Mindestintervall: 60 Sekunden.
- Ein Record wird nicht erneut geschrieben, wenn sich die Adresse nicht geändert hat.
- Fällige Ziele werden mit PostgreSQL `FOR UPDATE SKIP LOCKED` exklusiv beansprucht.
- Mehrere App-Instanzen führen dasselbe Ziel nicht gleichzeitig aus.
- Fehlversuche und letzter erfolgreicher Wert bleiben sichtbar.

## 8. Jobs

Unterstützte Jobtypen:

- `ddns`
- `provider-test`
- `health`
- `backup`

Jeder Lauf enthält:

- Start- und Endzeit
- Status und Meldung
- strukturierte Details
- eindeutige Correlation-ID

Fällige Jobs werden datenbankseitig gesperrt. Veraltete Sperren können nach 15 Minuten übernommen werden. Der Worker ist standardmäßig deaktiviert:

```text
Operations:WorkerEnabled=false
CADDY_UI_OPERATIONS_WORKER_ENABLED=false
```

Manuelle Ausführung aus der UI bleibt möglich und wird ebenfalls als Joblauf protokolliert.

## 9. Healthchecks

Zieltypen:

- `public`
- `upstream`

Pro Ziel werden festgelegt:

- HTTP(S)-URL
- erwarteter Statusbereich
- Timeout
- Aktivstatus

Jede Prüfung schreibt einen Verlaufseintrag und aktualisiert den aktuellen Zustand. Der Übergang zu `unhealthy` erzeugt eine Benachrichtigung.

## 10. Benachrichtigungen

Dauerhafte In-App-Benachrichtigungen sind immer die primäre Quelle. Externe Kanäle sind Ergänzungen:

- SMTP-E-Mail
- generischer HTTPS-Webhook
- Discord-Webhook
- Telegram-Bot

Ein fehlerhafter Kanal blockiert die anderen Kanäle nicht. Kanaltests aktualisieren sichtbaren Teststatus und Fehlertext.

## 11. Backups

Ein Backup enthält:

- PostgreSQL-Custom-Dump über `pg_dump`
- redigierten Diagnoseexport
- vorhandenes Root-Caddyfile
- vorhandenes Managed- und Shadow-Fragment
- Manifest und SHA-256-Digest

Das PostgreSQL-Passwort wird nur über die Prozessumgebung an `pg_dump` übergeben und erscheint nicht in Argumenten oder Manifesten.

Standardpfad:

```text
/data/caddy-ui/backups
```

Die Anzahl aufbewahrter Archive ist begrenzt. Ein Backup gilt erst nach erfolgreicher ZIP-Erstellung und Digest-Berechnung als erfolgreich.

## 12. Diagnoseexport

Der Diagnoseexport enthält:

- Runtime- und Betriebssysteminformationen
- aktive sichere Betriebsmodi
- Provider-Typ, Anzeigename und nicht geheime Konfiguration
- Namen der konfigurierten Secret-Felder
- Domains und Provider-Zuordnung
- Job-, Health- und DDNS-Zustand

Nicht enthalten:

- Secretwerte
- Secret-Referenzen
- Sitzungs- und Recovery-Tokens
- Data-Protection-Schlüssel
- Admin- oder Portal-Kennwörter

## 13. Produktionsfreigabe

Vor `active` müssen mindestens geprüft sein:

1. Provider-Verbindungstest für jede produktive Domain.
2. Shadow-DNS-Lauf für A/AAAA und einen Zertifikats-TXT-Record.
3. Wildcard-Preview und Caddy-Validierung.
4. kontrollierter Zertifikatslauf auf einer Testdomain.
5. DDNS-Änderung und Unchanged-Pfad.
6. Health-Fehler und Benachrichtigung.
7. Backup und Test-Wiederherstellung.
8. Dateirechte und persistente Volumes.
9. Worker-Sperre mit mehreren Instanzen.
10. dokumentierter Rollback auf den Python-Produktivpfad.
