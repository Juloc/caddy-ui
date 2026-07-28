# Caddy UI 2.0 – UI-Designvertrag

Status: verbindlich ab Phase 7  
Referenz: AE01 Fluent-/Windows-11-Arbeitsoberfläche

## Zielbild

Caddy UI ist ein schnelles Verwaltungswerkzeug, keine Marketingseite. Die Oberfläche soll ruhig, kompakt, eindeutig und ohne visuelle Unschärfe wirken. Bedienelemente müssen auf den ersten Blick als Bedienelemente erkennbar sein. Flächen dürfen nicht nur durch mehrere fast identische Weißtöne voneinander getrennt werden.

## Grundprinzipien

- servergerenderte Razor Pages; kein schweres SPA-Framework
- kurze Interaktionen und möglichst wenig JavaScript
- klare Informationshierarchie: Seite, Toolbar, Arbeitsbereich, Detail
- flache Arbeitsbereiche statt verschachtelter Kartenwände
- sichtbare Rahmen und Zustände statt dekorativer Schatten
- keine Gradienten, Glows, Glas-/Blur-Effekte oder unnötige Animationen
- Farbe dient Status, Auswahl und Aktion; nicht Dekoration
- alle Änderungen bleiben per URL, Formular und Browsernavigation nachvollziehbar

## Design Tokens

```text
Abstände:       4 / 8 / 12 / 16 / 20 / 24 / 32 px
Controls:       34 px Standard, 30 px kompakt
Radien:         4 px Controls, 8 px Panels, 12 px Dialog/Flyout
Schrift:        Segoe UI Variable / Segoe UI
Code:           Cascadia Code / Cascadia Mono
Tabellenzeile:  ca. 38 px komfortabel, 32 px kompakt
```

Semantische Farben werden über `--ui-*`-Tokens gepflegt. Light und Dark verwenden dieselben Bedeutungen, aber eigene Werte.

## Flächen und Kontrast

Light:

- Hintergrund ist ein leicht neutrales Grau
- Arbeitsflächen sind weiß
- Panelgrenzen sind sichtbar
- Hover und Auswahl unterscheiden sich klar

Dark:

- Hintergrund, Sidebar und Arbeitsflächen besitzen klar getrennte Helligkeitsstufen
- Textkontrast bleibt hoch
- Statusfarben werden nicht zu Neonflächen

Verboten ist eine Oberfläche, bei der Hintergrund, Cards, Eingaben und Buttons nahezu denselben Weißton ohne erkennbare Begrenzung besitzen.

## Controls

Primärbutton:

- genau eine dominante Aktion pro Arbeitsbereich
- gefüllte Akzentfläche
- eindeutiger Text, zum Beispiel `Route speichern` oder `Validieren & anwenden`

Sekundärbutton:

- helle/ruhige Fläche mit sichtbarem Rand
- für Navigation und reversible Nebenaktionen

Gefahraktion:

- rote semantische Darstellung
- explizite Bestätigung
- niemals direkt neben der Primäraktion ohne Abstand und Beschriftung

Eingaben:

- sichtbarer Rand
- sichtbarer Fokus
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

## Navigation und Status

- dunkle, kompakte Sidebar mit Gruppen
- aktiver Eintrag klar hervorgehoben
- Topbar zeigt Arbeitsbereich und globale Zustände
- feste Statusbar zeigt Betriebsbereitschaft und Schreibmodus
- Feedback darf das Layout nicht verschieben

## Bewegung und Leistung

- Animationen nur für Zustandsübergänge
- Standarddauer etwa 160 ms
- `prefers-reduced-motion` wird respektiert
- keine dauerhaften Animationen
- keine UI-Bibliothek oder Icon-Schrift nur für wenige Symbole
- keine regelmäßigen Vollseitenabfragen; Live-Daten über SSE

## Responsive Verhalten

- Desktop: volle Sidebar und dichte Tabellen
- mittlere Breite: kompakte Icon-Sidebar
- mobil: einspaltige Arbeitsbereiche, umbrechende Aktionen und horizontal scrollbare Tabellen
- keine versteckten Primäraktionen

## Abnahme

Eine neue Seite ist nur fertig, wenn:

1. Primäraktion und Nebenaktionen eindeutig erkennbar sind.
2. Inputs, Buttons und Panels auch in Light klar voneinander abgegrenzt sind.
3. Keyboard-Fokus sichtbar ist.
4. die Seite ohne unnötiges JavaScript funktioniert.
5. Dark Mode und reduzierte Bewegung funktionieren.
6. Tabellen und Formulare bei 760 px Breite noch bedienbar sind.
7. keine dekorativen Gradienten, Blur-Flächen oder Card-Wände eingeführt wurden.
