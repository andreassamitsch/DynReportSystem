# Round Table 2.0: native aufklappbare DynReport-Tabelle (ab 0.19.5)

Das native Tabellen-Visual hat neu \`PresentationMode = "Hierarchy"\`.
Pro Auftrag erscheint standardmäßig eine einzige Übersichtsgruppe.
Klick öffnet die Auftragspositionen, ein weiterer Klick die Aufgaben
je Position und Abteilung. Mehrere gleichzeitige Aufgaben bleiben erhalten.
Die Statusfelder stammen **nur aus dem Dataset**; in der Runtime wird keine
fachliche Definition von „offen“ oder „erledigt“ geraten.

## Einrichtung im Designer

Tabelle → Darstellung: **Auf-/zuklappbare Hierarchie (SSRS)**.
Ebenen: **Auftrag** und **Position**. \`Anfangs geöffnete Ebenen = 0\`.
Datenmodus ist lokal (Client), da alle Kinder einer Gruppe verfügbar sein
müssen. Paging erfolgt **über Aufträge** statt durch Abschneiden von Positionen.

Für die bestehende SQL-Datenbasis
\`Roundtable_2_0_Auftragsuebersicht_Drilldown.rdl\` / \`Roundtable_2_0_SQL.sql\`:

~~~json
{
  "Type": "table",
  "Id": "roundtable-overview",
  "Title": "Round Table 2.0 – Auftragsübersicht",
  "Dataset": "DataSet1",
  "Span": 12,
  "MobileSpan": 12,
  "Table": {
    "PresentationMode": "Hierarchy",
    "DataMode": "Client",
    "PageSize": 50,
    "Searchable": true,
    "Filterable": false,
    "Sortable": false,
    "Columns": [
      { "Field": "Abteilung", "Header": "Abteilung" },
      { "Field": "PositionAnzeige", "Header": "Aufgaben / letzter Abschluss" },
      { "Field": "OffenPosition", "Header": "Offen (Abteilung)" },
      { "Field": "LetztesPositionDatum", "Header": "Letzter Abschluss" }
    ],
    "Hierarchy": {
      "InitiallyExpandedDepth": 0,
      "Levels": [
        {
          "Field": "Auftrag",
          "Label": "Auftrag",
          "SubtitleField": "Kunde",
          "OpenCountField": "OffenAuftrag",
          "CompletedDateField": "LetztesAuftragDatum",
          "SummaryField": "AuftragAnzeige",
          "SummaryLabelField": "Abteilung",
          "SummaryDistinctField": "Abteilung",
          "SummarySortField": "AbteilungSortNr",
          "SummaryOpenCountField": "OffenAbteilung",
          "SummaryOpenOnly": true
        },
        {
          "Field": "Position",
          "Label": "Position",
          "OpenCountField": "OffenGanzePosition",
          "CompletedDateField": "LetztesPositionDatum"
        }
      ]
    }
  }
}
~~~

Diese JSON-Konfiguration gehört als \`DynVisual\` in die \`Pages[].Components[]\`
eines vorhandenen nativen Reportdokuments. Sie ist **kein vollständiges**
\`.dynreport\`-Paket. Die produktiven SQL-Verbindungsinformationen und das
SQL der Round-Table-Übersicht gehören nicht ins öffentliche Repository.

## Anzeige und Verhalten

- Geschlossen: Auftrag + Kunde + **offene Aufgaben insgesamt**.
  Darunter pro betroffener Abteilung eine zusammengefasste Zeile
  mit den offenen Aufgaben (\`AuftragAnzeige\`). Keine offenen Aufgaben:
  Abteilungen mit letztem Abschlussdatum.
- Auftrag geöffnet: Positionen (inklusive \`Kopf\`) mit Status
  und letztem Abschlussdatum.
- Position geöffnet: einzelne Datasetzeilen je Abteilung, mit dem
  \`PositionAnzeige\`-Text und allen Detailspalten. Mehrere Aufgaben in
  \`PositionAnzeige\` behalten Zeilenumbrüche.
- Suche gilt über alle Datasetfelder, auch verborgene. CSV exportiert
  die gefilterten Detailzeilen, unabhängig vom Aufklappzustand.
- Seitenwechsel zählt Auftragshauptgruppen. Keine Gruppe wird über die
  Seitenkante zerteilt. Die maximale Datasetgröße bleibt durch die
  bisherigen Runtime-Limits bestimmt.

**Datenvollständigkeit:** Bei \`Result.Truncated\` weist die Ansicht klar darauf hin,
dass die Gruppensummen nicht vollständig sein können. Es gibt kein
serverseitiges Hierarchie-Paging in 0.19.5. Für große Datasets die vorhandenen
Filter/Parameter einschränken oder später eine eigene serverseitige
Eltern-/Kind-Abfrage ergänzen. Das System schaltet niemals still auf eine
serverpaginiert-unvollständige Gruppendarstellung um.

Die Statussummen \`OffenAuftrag\` / \`OffenGanzePosition\` werden je Gruppe
per **MAX** statt SUM übernommen, denn die Werte werden im SQL bereits
je Auftrag/Position berechnet und über die Abteilungszeilen wiederholt.
\`LetztesAuftragDatum\`/ \`LetztesPositionDatum\` nutzen den neuesten Wert.

## Kompatibilität

- Schema bleibt **2.0**; \`Hierarchy\` ist optional und alte Tabellen
  (\`Rows\`, \`Grouped\`, \`Pivot\`) bleiben unverändert.
- Keine SQL-Migration. Bereits importierte native Berichte müssen
  lediglich im Designer auf die neue Präsentationsart umgestellt werden.
- Feature-Branch ist auf dem noch nicht gemergten **PR #14** aufgesetzt
  (inkl. der Chart-Korrektur aus PR #13). Beim Release zuerst die
  Abhängigkeit der PRs auflösen.
- Server-Setup 0.19.5 und Prüftest \`DynReportSystem.HierarchyRegression\`.
- Praxistest auf APP-01 mit echten Round-Table-Daten und Berechtigungen
  steht aus (SSRS-Vergleich, Filter, mobile Darstellung, Print/CSV).
