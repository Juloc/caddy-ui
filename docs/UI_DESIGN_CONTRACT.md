# Caddy UI – UI-Designvertrag

Status: verbindlich  
Referenz: Microsoft Fluent 2 und Windows 11

## Zielbild

Caddy UI ist ein schnelles Verwaltungswerkzeug, keine Marketingseite. Die Oberfläche wirkt ruhig, kompakt und eindeutig. Bedienelemente sind sofort erkennbar, Zustände werden verständlich erklärt und die Bedienung bleibt auf Desktop, Tablet und Mobilgeräten vollständig erhalten.

## Grundprinzipien

- servergerenderte Razor Pages; kein schweres SPA-Framework
- kurze Interaktionen und möglichst wenig JavaScript
- klare Informationshierarchie: Seite, Aktionen, Arbeitsbereich, Detail
- flache Arbeitsbereiche statt verschachtelter Kartenwände
- sichtbare Rahmen und Zustände statt dekorativer Effekte
- keine Gradienten, Glows oder unnötigen Animationen
- Farbe dient Status, Auswahl und Aktion; nicht Dekoration
- alle Änderungen bleiben per URL, Formular und Browsernavigation nachvollziehbar
- Feedback erscheint im Kontext und nicht als unnötiger Dialog oder Popup

## Design Tokens

```text
Abstände:       4 / 8 / 12 / 16 / 20 / 24 / 32 px
Controls:       36 px Standard, 30 px kompakt
Radien:         6 px Controls, 10 px Panels, 14 px Dialog/Flyout
Schrift:        Segoe UI Variable / Segoe UI
Code:           Cascadia Code / Cascadia Mono
Tabellenzeile:  ca. 38 px komfortabel, 32 px kompakt
```

Semantische Farben werden ausschließlich über `--ui-*`-Tokens gepflegt. Light und Dark verwenden dieselben Bedeutungen mit jeweils passenden Werten.

## Flächen und Kontrast

Light:

- Hintergrund ist ein leicht neutrales Grau
- Sidebar und Arbeitsflächen sind hell
- Panelgrenzen sind sichtbar
- Hover und Auswahl unterscheiden sich klar

Dark:

- Hintergrund, Sidebar und Arbeitsflächen besitzen klar getrennte Helligkeitsstufen
- Textkontrast bleibt hoch
- Statusfarben werden nicht zu Neonflächen

Verboten ist eine Oberfläche, bei der Hintergrund, Panels, Eingaben und Buttons ohne erkennbare Begrenzung ineinanderlaufen.

## Controls

Primärbutton:

- genau eine dominante Aktion pro Arbeitsbereich
- gefüllte Akzentfläche
- eindeutiger Text, zum Beispiel `Route speichern` oder `Validieren & anwenden`

Sekundärbutton:

- ruhige Fläche mit sichtbarem Rand
- für Navigation und reversible Nebenaktionen

Gefahraktion:

- rote semantische Darstellung
- explizite Bestätigung
- niemals direkt neben der Primäraktion ohne Abstand und Beschriftung

Eingaben:

- sichtbarer Rand und Fokus
- Label oberhalb des Feldes
- Hilfetext nur für relevante Bedeutung
- Fehler direkt am Feld oder als klare Zusammenfassung

Checkboxen und Schalter:

- enthalten eine konkrete Handlung und optional eine kurze Konsequenz
- keine vagen Beschriftungen wie `Aktiv` ohne Kontext

## Tabellen und Listen

- Tabellen für Requests, Routen, Benutzer und Revisionen
- wichtigste Spalte links, Aktionen rechts
- Zeilenhover, klare Header und kompakte Metadaten
- Status als semantisches Badge
- deaktivierte Einträge bleiben lesbar und werden nur gedämpft
- keine eigene Card pro Tabellenzeile
- breite Inhalte bleiben horizontal scrollbar

## Navigation und Anwendungsshell

- helle Sidebar als Standard, Dark-Variante über dieselben semantischen Tokens
- eine konsistente Outline-Iconfamilie mit 24-Pixel-Raster
- aktiver Eintrag klar hervorgehoben
- Sidebar auf Desktop ein- und ausklappbar
- mobile Navigation als vollständig bedienbare Off-Canvas-Sidebar
- Escape schließt die mobile Navigation; Fokus wird nachvollziehbar geführt
- Theme-Auswahl mit genau System, Hell und Dunkel bleibt unten in der Sidebar verankert
- Abmelden steht direkt neben der Theme-Auswahl
- Version steht im unteren Statusbereich der Sidebar
- Laufzeit- und Produktinformationen gehören auf die Seite `Über Caddy UI`
- keine dauerhafte Topbar und keine Statusanzeige mit erfundenem Bereitschaftszustand

## Bewegung und Leistung

- Animationen nur für Zustandsübergänge
- Standarddauer etwa 140 bis 180 ms
- `prefers-reduced-motion` wird respektiert
- keine dauerhaften Animationen
- keine UI-Bibliothek oder Icon-Schrift nur für wenige Symbole
- keine regelmäßigen Vollseitenabfragen; Live-Daten über SSE

## Responsive Verhalten

- Desktop: volle oder kompakte Sidebar und dichte Tabellen
- Tablet: einspaltige Arbeitsbereiche, Navigation bei Bedarf als Overlay
- Mobil: Off-Canvas-Navigation, einspaltige Inhalte und umbrechende Aktionen
- Primäraktionen bleiben sichtbar und mit Tastatur oder Touch erreichbar
- Tabellen und Diffs dürfen horizontal scrollen, ohne die Seite zu verbreitern

## Abnahme

Eine Seite ist nur fertig, wenn:

1. Primäraktion und Nebenaktionen eindeutig erkennbar sind.
2. Inputs, Buttons und Panels in Light und Dark klar abgegrenzt sind.
3. Keyboard-Fokus sichtbar ist.
4. die Seite ohne unnötiges JavaScript funktioniert.
5. System-, Hell- und Dunkelmodus funktionieren.
6. reduzierte Bewegung respektiert wird.
7. Tabellen und Formulare bei 760 Pixel Breite bedienbar bleiben.
8. leere, fehlerhafte und ladende Zustände verständlich sind.
9. keine dekorativen Effekte oder unnötigen Popups eingeführt wurden.
