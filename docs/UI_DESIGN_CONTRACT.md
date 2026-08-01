# Caddy UI – UI-Designvertrag

Status: verbindlich  
Referenz: Microsoft Fluent 2 Web und Windows 11

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
- Microsoft Fluent 2 Web ist das anwendungsweite Designsystem. Es wird als Design- und Implementierungsstandard verwendet, nicht nur als visuelle Inspiration.
- Razor Pages erzeugen semantisches HTML; React, SPA-Navigation und clientseitiges Rendering gehören nicht zur Anwendung.
- Fluent Web Components sind nur als progressive Ergänzung zulässig. Eine Kernfunktion darf nie von einem Custom Element oder JavaScript abhängen.

## Design Tokens

```text
Abstände:       4 / 8 / 12 / 16 / 20 / 24 / 32 px
Controls:       36 px Standard, 30 px kompakt
Radien:         6 px Controls, 10 px Panels, 14 px Dialog/Flyout
Schrift:        Segoe UI Variable / Segoe UI
Code:           Cascadia Code / Cascadia Mono
Tabellenzeile:  ca. 38 px komfortabel, 32 px kompakt
```

Semantische Farben werden ausschließlich über `--ui-*`-Tokens gepflegt. Diese Tokens bilden eine zentrale Alias-Schicht auf den Fluent-2-Tokenrollen: globale Fluent-Werte werden nicht direkt in Seiten oder Komponenten verwendet. Light und Dark verwenden dieselben Bedeutungen mit jeweils passenden Werten.

| Caddy-UI-Alias | Fluent-2-Rolle | Verwendung |
| --- | --- | --- |
| `--ui-bg` | `colorNeutralBackground2` | Seitenhintergrund |
| `--ui-surface` | `colorNeutralBackground1` | Arbeitsfläche, Eingabe, Dialog |
| `--ui-surface-subtle` | `colorNeutralBackground3` | sekundäre Fläche, Hover |
| `--ui-text`, `--ui-text-muted` | `colorNeutralForeground1`, `colorNeutralForeground2` | Text-Hierarchie |
| `--ui-border` | `colorNeutralStroke1` | sichtbare Grenzen |
| `--ui-focus` | `colorStrokeFocus2` | Tastaturfokus |
| `--ui-accent` | `colorBrandBackground` | Primäraktion und Auswahl |
| `--ui-success`, `--ui-warning`, `--ui-danger`, `--ui-info` | Fluent-Statusfarben | Status mit Text und Icon |
| `--ui-shadow-flyout`, `--ui-shadow-dialog` | `shadow28`, `shadow64` | ausschließlich Overlay-Flächen |

Keine Komponente enthält eigene Hexfarben, individuelle Schatten oder abweichende Abstandsskalen. Typografie, Radius, Stroke und Bewegung folgen ebenfalls dieser Alias-Schicht.

## Themes und Kontraste

- Die Auswahl enthält exakt `System`, `Hell` und `Dunkel`; `System` ist der Standard und folgt `prefers-color-scheme`, statt den aktuell ermittelten Wert zu speichern.
- Eine ausdrückliche Auswahl wird pro Benutzer gespeichert und über `data-theme` angewandt. `color-scheme` wird passend gesetzt, damit native Controls korrekt mitwechseln.
- `forced-colors` und browserseitige Kontrastmodi werden nicht überschrieben.
- Standardtext erreicht mindestens 4,5:1 Kontrast; großer Text mindestens 3:1. Interaktive und nichttextliche Bedienelemente erreichen gegenüber angrenzenden Farben mindestens 3:1.

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

Komponentenmatrix:

| Aufgabe | Verbindliches Muster | Nicht verwenden |
| --- | --- | --- |
| Navigation | `<a>` in `<nav>`, aktiver Eintrag sichtbar und mit `aria-current` | Buttons für Navigation |
| Aktion | `<button>` mit konkretem Verb | Link, der einen Zustand ändert |
| Dateneingabe | `<label>` plus natives Feld und feldnaher Fehler | Platzhalter als Label |
| Auswahl | natives `<select>` für Formulare, klar beschriftete Checkboxen/Radio-Gruppen | unbeschriftete Icon-Auswahl |
| Datenübersicht | semantisches `<table>` mit Headern und horizontalem Container | Card pro Tabellenzeile oder eine JS-Pflicht-Grid-Komponente |
| Status | Badge/Text/Icon mit semantischer Farbe | Farbe oder Icon allein |
| Rückmeldung | Feldfehler, Message Bar oder Toast je nach Dringlichkeit | Dialog für gewöhnliches Feedback |
| Zusatzinformation | sichtbarer Hilfetext, Tooltip nur ergänzend | essenzielle Information nur im Tooltip |

Buttons reagieren mit ihrer Beschriftung auf die Aufgabe. `Abbrechen` verwirft einen begonnenen Vorgang; `Schließen` beendet nur eine Oberfläche. Toolbars umbrechen nicht: seltene oder nicht passende Aktionen wandern in ein klar beschriftetes Overflow-Menü.

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

Die Shell nutzt `<aside>`, `<nav>` und `<main>` sowie einen Skip-Link. Die Desktop-Navigation ist 260 px breit und kann kompakt werden. Unterhalb der 1024-px-Desktopklasse wird sie als Overlay geöffnet, damit der dichte Arbeitsbereich ausreichend Breite behält; Escape schließt das Overlay und der Fokus kehrt zum Auslöser zurück. Aktionen, die nur bei Hover sichtbar sind, bleiben im DOM und zusätzlich über Menü oder Toolbar erreichbar.

## Dialoge, Meldungen und Zustände

- Erstellen und Bearbeiten erfolgt auf Desktop in einem Dialog mit Titel, Inhalt und höchstens drei Footer-Aktionen. Kleine Bildschirme verwenden dieselbe Aufgabe als Vollbildoberfläche.
- Beim Öffnen erhält das erste sinnvolle Bedienelement den Fokus; modale Dialoge halten den Fokus; beim Schließen kehrt er zum Auslöser zurück. Dialoge dürfen nicht verschachtelt werden.
- Zerstörerische Aktionen nutzen eine explizite Bestätigung mit konkreter Objektbezeichnung und einem sicheren Abbruchweg.
- Validierungsfehler stehen direkt am Feld. Page- oder bereichsweite Fehler erscheinen als Message Bar; nichtkritische Bestätigungen dürfen als kurzlebiger Toast erscheinen.
- Jede Seite gestaltet leere, ladende, teilweise fehlerhafte, nicht berechtigte und offline/unterbrochene Zustände bewusst. Skeletons oder Live-Regionen dürfen den Tastaturfokus nicht stören.

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

Die Fluent-2-Größenklassen geben die Orientierung: klein 320–479 px, mittel 480–639 px, groß 640–1023 px und Desktop ab 1024 px. Die Anwendung muss bei 320 px beziehungsweise 400 % Zoom ohne Informationsverlust oder Seiten-Scrollen in der Breite funktionieren; Text-Zoom bis 200 % darf nichts abschneiden. Kompakte Desktop-Controls dürfen 30/36 px hoch sein, interaktive Touch-Ziele erhalten auf Mobilgeräten mindestens 44 × 44 px.

## Semantik und Zugänglichkeit

- Verwende echte HTML-Landmarks, logische Überschriftenebenen, Tabellen-Header, Formularlabels und `button`/`a` entsprechend ihrer Funktion.
- Jeder Tastaturfokus ist deutlich sichtbar. Fokus folgt einer nachvollziehbaren Reihenfolge und geht beim Schließen temporärer Oberflächen nicht verloren.
- Reine Icons haben einen deutschen zugänglichen Namen; redundante dekorative Icons werden vor Assistenztechnologien verborgen.
- Tooltipps enthalten nur ergänzende Klarstellung und sind per Fokus erreichbar; ihr Inhalt wird über `aria-describedby` verknüpft.
- Statusfarben werden durch Klartext ergänzt. Live-Regionen sind sparsam: Warnung/Fehler dringlich, Information/Erfolg höflich.

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
10. semantisches HTML, Labels, Landmarks, Fokus-Rückgabe und Textalternativen geprüft sind.
11. die Seite bei 320 px/400 % Zoom und 200 % Text-Zoom ohne Informationsverlust nutzbar bleibt.

## Verbindliche Quellen

- [Fluent 2 Design Tokens](https://fluent2.microsoft.design/design-tokens)
- [Fluent 2 Color Tokens](https://fluent2.microsoft.design/color-tokens)
- [Fluent 2 Typography](https://fluent2.microsoft.design/typography)
- [Fluent 2 Layout and responsive design](https://fluent2.microsoft.design/layout)
- [Fluent 2 Accessibility](https://fluent2.microsoft.design/accessibility)
- [Fluent 2 Navigation](https://fluent2.microsoft.design/components/web/react/core/nav/usage)
- [Fluent 2 Dialog](https://fluent2.microsoft.design/components/web/react/core/dialog/usage)
- [Fluent UI Web Components overview](https://learn.microsoft.com/en-us/fluent-ui/web-components/)
