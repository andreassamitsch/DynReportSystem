# SQL-Datenquellen im DynReport-Portal (ab 0.19.7)

## Ziel

Die SQL-Connection für einen nativen \`.dynreport\` wird zentral anhand des
\`DataSources[].ConfigKey\` eingerichtet. Für Round Table 2.0 aus der
geprüften Paketdatei ist das **\`oxaion-stg\`**. Der Fehler
\`Datenquelle 'oxaion-stg' ist nicht konfiguriert\` bedeutet, dass eine
Konfiguration für diesen Schlüssel fehlt; Report-SQL und Paket sind deswegen
nicht zwangsläufig fehlerhaft.

Portal → **SQL-Datenquellen** → bei benötigtem Schlüssel **Einrichten**:

1. Kennung: \`oxaion-stg\` (genau wie im Paket).
2. Server/Instanz und **SQL-Datenbanknamen** der Oxaion-Staging-Umgebung
   eintragen. Server- und Datenbanknamen nicht verwechseln.
3. Anmeldung: **Windows (IIS-Anwendungspool)** für einen mit Leserechten
   ausgestatteten Domänen-/Dienstbenutzer; andernfalls SQL-Benutzer und Passwort.
4. **Encrypt = Ein** und nach Möglichkeit **TrustServerCertificate = Aus**.
   Ein nicht vertrauenswürdiges Serverzertifikat sollte langfristig korrekt
   ausgestellt werden, nicht permanent umgangen werden.
5. **Verbindung testen**, danach **Testen und speichern**.
6. Round-Table-Bericht im Browser erneut ausführen.

## Berechtigungen und Datenfluss

- Nur authentifizierte Benutzer mit **Manage auf allen Hauptordnern** können
  die serverweiten Verbindungen sehen/ändern. Report-Designer-/Publish-Rechte
  reichen nicht aus.
- Die Portalverbindung hat Vorrang vor \`DataSources:<ConfigKey>\` aus
  \`appsettings.Production.json\` und vor dem alten Fallback.
- Vorhandene Appsettings-Verbindungen bleiben weiterhin funktionsfähig.
- SQL-Passwörter werden nie an Blazor als Bestandswert zurückgegeben:
  Beim Bearbeiten bleibt das Passwortfeld leer; leer heißt „behalten“.
- Neu gesetzte Passwörter werden vor dem Schreiben mit
  **Windows DPAPI (LocalMachine)** verschlüsselt. Die Registry-Datei liegt
  unter \`%ProgramData%\DynReportSystem\Config\sql-datasources.json\`.
  Beim Schreiben wird die Windows-Datei-ACL ausdrücklich auf die aktive
  Dienstidentität, **SYSTEM** und **Administratoren** beschränkt.
  **Diese Datei nicht auf einen anderen Server kopieren:** Das DPAPI-Secret
  kann ohne Machine-Key nicht entschlüsselt werden.
- Die Oberfläche gibt niemals ein gespeichertes Passwort oder einen
  ConnectionString zurück. Logs enthalten nur die Quellenkennung.
- Der Verbindungstest prüft SQL-Anmeldung, Zieldatenbank und \`SELECT 1\`.
  Das garantiert **nicht**, dass der Berichtsbenutzer auf alle Fach-Tabellen
  zugreifen darf; danach den Bericht selbst testen.
- SQL-Konten und Dienstidentitäten sollten **nur SELECT** auf benötigten
  Tabellen/Views besitzen. Keine db_owner/sysadmin Rechte für Berichte.
- Die ProgramData-Datei ist vom Installationsverzeichnis getrennt und bleibt
  bei regulären Programmupgrades erhalten.

## Administrationsumfang

Anlegen, Bearbeiten, Verbindungstest und Löschen (mit zusätzlicher
Bestätigung). Löschen eines Portal-Eintrags löscht nicht die Reportpakete;
existiert eine alte Serverkonfiguration für denselben Schlüssel, wird diese
wieder verwendet. Sonst melden die Reports eine fehlende Datenquelle.

## Rollout / Abhängigkeit

0.19.7 enthält die 0.19.6-Ordnerverwaltung sowie die 0.19.5-Hierarchietabellen.
Wie bisher wird der Windows-Installer auf APP-01 installiert. Kein SQL-Schema-
Upgrade und keine Reportpaket-Migration erforderlich. GitHub CI muss den
Build und das Setup erfolgreich prüfen. Die Live-Verbindung kann erst
auf APP-01 mit dem tatsächlichen SQL-Server und Datenbankrechten geprüft werden.
