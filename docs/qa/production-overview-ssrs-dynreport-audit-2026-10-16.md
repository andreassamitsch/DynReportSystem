# Produktionsübersicht TV – fachlicher SSRS/DynReport-Abgleich, 16.10.2026

## Abgleichsgrundlage und Grenzen
- Vorliegend: Screenshot des bisherigen SSRS-Berichts, Screenshots des DynReport TV 2.2.0, `.dynreport` 2.2.0 inklusive SQL und Ausdrucksbaum sowie die historische Analyse des Original-SSRS-Berichts.
- **Nicht verfügbar:** Original-RDL mit den endgültigen SSRS-Ausdrücken und gleichzeitiger produktiver Dataset-Export von beiden Ansichten. Daher ist die numerische und farbliche **1:1-Gleichheit mit SSRS nicht nachgewiesen**. Screenshots verschiedener Uhrzeiten dürfen nicht als Zahlenvergleich behandelt werden.
- **Unverändert bei 0.19.4 / TV 2.2.1:** Report-SQL, Parameter, Formelbaum, Berechtigungen, Datenquellen und Heatmap-Schwellen. Geändert werden Kopfbereich, die Höhen und die Aufteilung bereits vorhandener Texte.

## Ausdrucksprüfung der aktuell gelieferten Berichtsversion 2.2.0

| Anzeige | Quelle / Formel in DynReport | Vorläufiges Ergebnis |
|---|---|---|
| Bereich, Maschine, Auftrag, Artikel, Kunde, Arbeitsgang | Dataset `MasAbteilung, MaschineID, Auftrag, Artikel/ArtikelNr, Kunde, AGNr, Vorgang` | übernommen |
| AG-Gutmenge / Auftragsmenge | `first(AgGutmenge) / first(ORDERAMOUNT)` | jetzt sichtbar; unabhängig von Schichtmenge |
| Status, Statusbeginn | letzter aktueller `Stillstand/BEGINDATE`, sonst letzter Datensatz | plausible Zuordnung; aktuelle Statushistorie mit RDL vergleichen |
| Personal | aktive Bediener, sonst bekannte Bediener | plausibel; Mehrfachanmeldungen prüfen |
| EM | Maximum aus `EMpruefungState` | prüfbedürftig bei gemischten historischen Werten |
| Rüstzeit h Ist/Soll | `first(RüstzeitIst)/first(RüstzeitSoll)` | direkt aus Dataset |
| Takt Schicht Ist | `sumDistinct(Duration bei Status Produktion) / max(AVG(SumRELEASEDCOUNT > 0), 1)` | **Null-Stück-Fallback** beachten |
| Takt Schicht Soll | `first(TaktzeitSoll)` | direkt aus Dataset |
| Stk Schicht Ist | `sum(RELEASEDCOUNT)` | **mögliche Doppelzählung** bei SQL-Join-Vervielfachung per Periode/Bediener prüfen |
| Stk Schicht Soll | Produktionsminuten / Solltakt | Ausdruck plausibel, SSRS-RDL erforderlich |
| Takt Gesamt Ist/Soll | `first(TaktzeitIst)/first(TaktzeitSoll)` | direkt aus Dataset |
| Behältertakt Ist | seit letzter Meldung aggregierte Produktionszeit, sonst gesamte Produktionszeit | 346-Minuten-Werte ohne Meldung möglich |
| Behältertakt Soll | `max(first(BehälterTaktSoll), 60)` | Mindestwert 60 Minuten |
| letzte Behältermeldung | `max(FINISHEDON)`, Menge/Person des letzten Datensatzes | plausibel; gleiche FINISHEDON-Werte prüfen |
| ungeplante Stillstandszeit h | `AVG(AG_ungeplanteStillstände)/60` | **kumulierte AG-Zeit, nicht reine Schichtzeit** |
| KPI ungepl. Stillstand | Summe der gruppierten AG-Zeiten | AG-Summen können bei mehrfach belegten Maschinen wiederholt wirken |

## Besonders auffällig: 346 Minuten
Beim Start der aktuellen Schicht seit 06:00 ergeben sich um 11:46 ca. 346 Minuten. Wenn der gemeldete Schicht-Ausstoß 0 ist, verwendet die aktuelle Formel den Ersatznenner 1. Das ergibt rechnerisch `346 / 1 = 346 min/Stk`, **ohne einen tatsächlich gemessenen Stücktakt**. Auch der Behältertakt kann ohne letzte Meldung auf die gesamte Produktionszeit zurückfallen. Vor Änderung der Formel müssen wir die produktive SSRS-RDL und Datasetzeilen der gleichen Maschine bei gleichem Zeitpunkt vergleichen.

## Farblogik (0.19.3 und 0.19.4)
- Positiver Sollwert erforderlich, sonst keine Intensitätsfarbe.
- Bei `LowerIsBetter`: Ist > Soll wird von gelb bis rot stärker markiert. Bei `HigherIsBetter`: Ist < Soll wird stärker markiert.
- Gut/auf Ziel: grün. Bis 10 % Abweichung: gelb, darüber bis 100 %: zunehmend orange/rot, dann maximale Intensität.
- **Dies ist eine kontinuierliche pragmatische Heatmap, keine nachgewiesene bitgleiche Kopie des historischen SSRS-`Code.MixColors`.** Gleichheit erst nach Einsicht in die RDL bestätigen.

## Prüfverfahren beim Praxistest
1. Bericht und SSRS mit **exakt gleichen Parametern**, identischer DB/Datenquelle und unmittelbar aufeinanderfolgender Ausführung vergleichen; idealerweise dieselben Datasetzeilen exportieren.
2. Jede Maschine/AG getrennt vergleichen; Stichprobe: ein laufender AG mit >0 Stück, ein AG mit 0 Stück, eine Maschine mit mehreren Arbeitsgängen, eine überfällige Behältermeldung, eine EM-NOK-Meldung und ein Maschinenbruch.
3. Pro Fall: AG-Gutmenge, Schichtstückzahl, Produktionsminuten, SumRELEASEDCOUNT, Solltakt, beide Takte, Behälter, Rüstzeit, EM, Stillstand, Farbklasse/Grad notieren.
4. Die 0-Stück-/Join-Duplikat-Abweichungen mit dem Fachbereich bewerten: den bisherigen SSRS-Zustand bewusst übernehmen **oder** fachlich korrekt ändern. Nie ohne Abnahme Produktivformeln ändern.
5. Auf 1920×1080 bei 100 % Browserzoom Anzahl sichtbarer Zeilen, Lesbarkeit, Tablet- und Handy-Darstellung prüfen.

## Technische Tests
- Bestehender CI-Test der PR-#13-Stapelsummen weitergeführt.
- Neue Checks: Richtung und Verlauf der Heatmap, fehlende Sollwerte neutral, deduplizierte Statusminuten bei Mehrfachzeilen und expliziter `346 / max(0,1)`-Fallback.
- Tests ersetzen **keine** SSRS-Äquivalenzprüfung gegen die Original-RDL.
