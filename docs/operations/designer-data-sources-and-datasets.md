# DynReport Designer – DataSource versus Dataset (0.19.10)

## Fehlerbild (Round Table 2.0)

Unter **Designer → Daten → DataSet1** zeigte die Auswahl **Datenquelle**
nur die im jeweiligen \`.dynreport\`-Paket mitgelieferte Referenz
\`oxaion-stg (oxaion-stg)\`. Eine auf APP-01 bereits im SystemAdmin-Portal
eingerichtete SQL-Verbindung war nicht auswählbar.

**Zwei Konzepte dürfen nicht verwechselt werden:**

- **Datenquelle:** eine serverseitig von SystemAdmins verwaltete SQL-Verbindung
  (Server, Datenbank, Anmeldedaten, TLS). Reports verwenden lediglich
  \`ConfigKey\`, niemals Login-Credentials im Paket.
- **Dataset:** SQL-Abfrage samt Parametern und ausgegebenen Spalten;
  \`DataSet1\` im nativen Bericht ist nicht automatisch dasselbe wie ein
  aus SSRS importiertes Shared Dataset.

## Was neu ist

### 1. Zentrale Datenquelle zuordnen

Designer → Daten → DataSet1 → **Zentrale SQL-Verbindung**:
SystemAdmins können aus den auf dem Server zentral gespeicherten
Verbindungsschlüsseln wählen. Es werden die sicheren Schlüsselnamen,
aber keine Kennwörter oder ConnectionStrings, in die UI übergeben.

Beim Wechsel legt DynReport eine **separate Paketreferenz** an und
ändert \`dataSet.DataSourceId\`. Bestehende andere Datasets, welche
die ursprüngliche Datenquelle verwenden, bleiben unverändert.

Die **SQL-Abfrage und Dataset-ID bleiben unverändert.** Das
ist die richtige Aktion, wenn der Bericht technisch mit der falschen
Datenbank oder SQL-Verbindung verbunden war.

Nur der in \`%ProgramData%\DynReportSystem\Security\system-administrators.json\`
festgelegte SystemAdmin darf andere zentrale Verbindungen zuordnen.
Report-EditData/Manage allein erlaubt dies nicht.

### 2. Dataset aus anderem nativen Paket übernehmen

Falls **der SQL-Text selbst falsch** ist, kann ein SystemAdmin, der
**EditData/Publish/Manage** auf beide betroffenen Berichte hat, im
Designer ein Dataset eines **anderen installierten nativen
\`.dynreport\`-Pakets** auswählen und nach einer zweiten Bestätigung
in das aktuelle Dataset übernehmen.

Dabei werden SQL-Datei, Datenquelle-ConfigKey, Parameterbindung,
CommandType und erwartete Felder kopiert. Fehlende Reportparameter
werden vor der Übernahme gemeldet. Wird dieselbe SQL-Datei von einem
weiteren Dataset verwendet, erhält das ersetzte Dataset eine eigene
SQL-Datei (Copy-on-write), damit die anderen Datasets unverändert bleiben. Die Ziel-Dataset-ID bleibt
unverändert, sodass vorhandene Visualisierungen weiterhin darauf
zeigen. Geänderte Parameter und Spalten müssen in der Vorschau
geprüft werden. Bei „Speichern“ greift die bestehende Revisionierung.

**Nicht enthalten:** Automatische Übernahme eines historischen SSRS
**Shared Dataset** aus dem SSRS-Migrationskatalog (RSD) ist noch
keine native Dataset-Bibliothek. Für solche Datensätze ist weiterhin
eine explizite RSD→native SQL-Dataset-Konvertierung nötig.

## Tests auf APP-01

1. Prüfen, ob das korrekte zentrale Datenquellenprofil im SystemAdmin-Portal
   gespeichert ist (z. B. \`OxaionSTG\`, tatsächlicher Name hängt von
   der Installation ab).
2. Designer → Daten → DataSet1: richtige **zentrale SQL-Verbindung**
   wählen; SQL muss unverändert bleiben.
3. Alternativ anderes **natives** Dataset auswählen,
   Bestätigen und SQL/Parameter kontrollieren.
4. Speichern, Revision erzeugen, Vorschau. Bei falschem Ergebnis
   unter „Versionen“ die ältere Revision wiederherstellen.
5. Mit Report-Manager ohne SystemAdmin versuchen: er darf keine
   fremde zentrale Verbindung übernehmen.
