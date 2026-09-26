# Analyse: /Reports/TV/Produktion Übersicht TV

Quelle: produktive SSRS-RDL `ssrs-report-669cc17b77203a674e03.rdl`.

## Zweck des Originalberichts

Der Bericht ist kein klassischer Analysebericht, sondern eine breite **Live-Produktionsübersicht für TV/Shopfloor**.  
Die zentrale Aussage ist eine Zeile je aktuell relevantem Arbeitsgang/Maschine mit sofort erkennbaren Abweichungen.

Der Berichtstitel wechselt nach aktueller Schicht:

- 06:00–14:00
- 14:00–22:00
- 22:00–06:00

Zusätzlich wird der Ausführungszeitpunkt angezeigt.

## Parameter

| Parameter | Originalfunktion |
|---|---|
| Start | optionaler Beginn; bei NULL bestimmt SQL den aktuellen Schichtbeginn |
| Ende | optionales Ende; bei NULL bestimmt SQL das aktuelle Schichtende |
| MASCHINE_STANDORT | MultiSelect; Standard: Drehen, Fräsen, Erodieren |
| nurAktive | Standard `-1 = ja`; SQL filtert auf aktuelle Arbeitsgänge |

## Daten

Primär verwendet wird `HoldupReasonHistory` aus `/Syncos PRD`.

Die zweite RDL-Datenmenge `BedienzeitenauswertungV3` ist im sichtbaren Bericht nicht gebunden und wurde deshalb in der optimierten Darstellung nicht als eigener Bereich verwendet.

## Originale fachliche Hierarchie

Die SSRS-Tablix-Gruppierung lautet:

1. `MasAbteilung`
2. `MaschineID`
3. `Artikel + Auftrag`
4. `AGNr`
5. versteckte Status-/Bediener-Detailgruppen für die Berechnung

Diese Hierarchie wird im DynReport-Board beibehalten.

## Sichtbare Originalspalten und Bedeutung

1. **Bereich**
2. **Maschine**
3. **Auftrag / Artikel / Kunde**
4. **Arbeitsgang**
5. **Status**
6. **Personal angemeldet**
7. **EM-Prüfung**
8. **Rüstzeit Ist / Soll**
9. **Takt Schicht Ist / Soll**
10. **Stk aktuell Ist / Soll**
11. **Takt Gesamt Ist / Soll**
12. **Behältertakt Ist / Soll**
13. **Letzte Behältermeldung Zeit / Menge / Person**
14. **ungeplante Stillstandszeit h**

## Statusfarben aus der RDL

Die Farben stammen aus `Code.GetColor()` des Originalberichts und wurden in die
manuell erstellte DynReport-Datei übernommen.

Beispiele:

- Produktion: `#32cd32`
- Rüsten: `#8fade3`
- Werkzeugbruch: `#e33e90`
- Maschinenbruch: `#f75e5e`
- Wartung: `#c26fff`
- Pause: `#CD6600`
- Erstmusterprüfung: `#29c3b4`

## AG-Status

Das Original verwendet ein StateIndicator-Gauge:

- `3` → Play / grün
- `5` → Pause / türkis
- `7` → abgeschlossen / Check

DynReport stellt diese Zustände als kompakte Statussymbole direkt bei der Maschine dar.

## Abweichungslogik

### Rüstzeit

`RüstzeitIst / RüstzeitSoll`.

Im Original wird bei Überschreitung die Zelle zunehmend rot gemischt, andernfalls grün.

### Takt Schicht

Das Original berechnet die Produktionsdauer der Schicht aus den Produktions-Statusintervallen
und teilt durch die freigegebene Menge. Ergebnis wird gegen `TaktzeitSoll` bewertet.

### Stück aktuell

Original:

- Ist = `Sum(RELEASEDCOUNT)`
- Soll = Produktionsdauer der Schicht / `TaktzeitSoll`

### Takt Gesamt

`TaktzeitIst / TaktzeitSoll`.

### Behältertakt

Wenn seit der letzten Behältermeldung die Sollzeit überschritten ist und der Arbeitsgang aktuell
ist, zeigt das Original eine gelbe Warnung/❗.

Der Sollwert wird mindestens mit 60 Minuten angesetzt.

### Erstmusterprüfung

Originale Farblogik:

- State 1 → grün
- State 2 → orange
- State 3 → rot

## Manuelle DynReport-Optimierung

Die neue Version behandelt den Bericht bewusst als Shopfloor-Board:

- aktuelle Schicht im Kopf
- Auto-Run
- 60-Sekunden-Aktualisierung
- kompakte KPIs für Maschinen/Produktion/Rüsten/Warnungen/Stillstand
- Bereichssektionen
- eine dicht lesbare Maschinen-/Arbeitsgangzeile auf TV/PC
- Kartenlayout auf Mobilgeräten
- Originalstatusfarben
- Abweichungen direkt farblich erkennbar
- Warnungen pro Arbeitsgang gebündelt
- technische Dataset-Ansicht bleibt als Fallback verfügbar

Die Datenabfrage, Parameter und SSRS-Berechtigungen werden **nicht** in der
`.dynreport`-Datei dupliziert. Die Datei definiert ausschließlich die optimierte
Darstellung auf Basis des vorhandenen migrierten SSRS-Berichts.
