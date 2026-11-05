# DynReport SystemAdmins – getrennte Serveradministration (0.19.9)

## Architektur und Berechtigungen

- **SystemAdmin** ist keine Berichts-/Ordner-Berechtigung und wird
  **nicht** aus \`Manage\`, SSRS, dem Installationsbenutzer oder AD
  Domain Admins automatisch abgeleitet.
- SystemAdmins dürfen die **zentralen SQL-Datenquellen** unter
  \`/admin/datasources\` verwalten. Die Funktion ist im Reporting-Portal
  nur für Mitglieder der serverseitigen SystemAdmin-Liste sichtbar.
- Ordner-/Berichtsrechte (View, Run, Edit, Publish, Manage) bleiben separat.
  \`Manage\` auf **allen** Ordnern führt zu **keinem** SystemAdmin-Recht.
- **SystemAdmins vergeben/entziehen geht nur lokal auf APP-01**
  (oder in einer durch den Serveradministrator kontrollierten
  administrativen Remotesitzung). Es gibt **keinen HTTP-Endpunkt und
  keine Portalmaske** zum Verwalten dieser Liste.
- Windows SSO: Nur die SID des angemeldeten Windows-Benutzers oder
  dessen Windows-Gruppen-SIDs werden geprüft. Eine bloße Zeichenfolge
  wie \`User.Identity.Name\` reicht nicht. Das Skript übersetzt ein
  Windows- oder AD-Konto beim Hinzufügen in die SID, damit
  Umbenennungen keine Zugriffsänderung verursachen.

## Einrichtung auf APP-01

Die Installationsdatei liefert
\`C:\Program Files\DynReportSystem\Tools\Manage-DynReportSystemAdmins.ps1\`.
Auf **APP-01** Windows PowerShell/PowerShell als **Administrator** öffnen:

~~~powershell
$tool = 'C:\Program Files\DynReportSystem\Tools\Manage-DynReportSystemAdmins.ps1'

# Empfohlen: eine eigene AD-Gruppe statt individueller Konten
& $tool -Action Add -Account 'CNC-DOMAIN\DynReport-SystemAdmins'

# Alternativ ein einzelner Benutzer für die Erstinbetriebnahme
& $tool -Action Add -Account 'CNC-DOMAIN\Fuchshofer139'

# Aktuelle Zuordnungen anzeigen
& $tool -Action List

# Einzelne Zuordnung entfernen (letzte kann nicht entfernt werden)
& $tool -Action Remove -Account 'CNC-DOMAIN\Fuchshofer139'
~~~

Die tatsächlich verwendete AD-Gruppe muss vorher existieren. Die
angegebenen Namen sind Beispiele – **die Installation trägt niemanden
automatisch ein.** Wer die Gruppe in Active Directory verwaltet,
kontrolliert damit auch ihre Mitglieder und muss entsprechend
vertrauenswürdig sein. Neue Gruppenmitgliedschaften benötigen
normalerweise eine neue Windows-Anmeldung, damit das Token aktualisiert wird.

Bei fehlender Konfiguration gibt es zunächst **keinen** SystemAdmin.
Dies ist beabsichtigt (fail-closed); die initiale Vergabe erfolgt auf
dem Server über das Skript.

Die Liste liegt unter
\`%ProgramData%\DynReportSystem\Security\system-administrators.json\`.

- Das Verzeichnis **Security** ist vom durch den IIS-Anwendungspool
  beschreibbaren \`Config\`-Verzeichnis strikt getrennt.
- Das Installationsprogramm setzt die Windows-ACL auf
  SYSTEM + lokale Administratoren **Vollzugriff** und
  \`IIS AppPool\DynReportSystem\` **nur Lesen/Ausführen**.
- Das Skript erstellt Änderungen mit administrativen Rechten und
  verschließt die Datei ebenfalls mit diesen NTFS-ACLs.
- Nach Änderung ist kein IIS-Neustart nötig. Jede Prüfung liest die
  aktuelle Konfiguration vom Datenträger.
- Die JSON-Datei allein manuell zu ändern wird nicht empfohlen;
  zum Schutz der Sicherheits-ACLs stets das serverlokale Skript verwenden.

## SQL-Datenquellen danach einrichten

Als eingerichteter SystemAdmin am Portal anmelden →
**SQL-Datenquellen** → \`oxaion-stg\` → Einrichten.
Verbindung unter der ursprünglichen \`ConfigKey\`-Kennung speichern.
SQL-Anmeldung und Leserechte des IIS-Dienstkontos bzw.
SQL-Kontos gesondert prüfen.

## Betrieb und Upgrade

- Keine Datenbankschemaänderung, keine Migration vorhandener Berichte.
- Neue SystemAdmin-Liste ist unabhängig von Ordnerrechten und bleibt im
  ProgramData-Sicherheitsverzeichnis über Installer-Upgrades erhalten.
- Für Notfallzugriff benötigt die IT lokalen Administratorzugriff auf
  APP-01; bloßes Manage im Webportal reicht nicht.
- Sichern/Wiederherstellen der Liste ausschließlich mit Schutz der
  NTFS-ACLs. Vor einer Wiederherstellung muss das Tool erneut die
  Schutzregeln auf der Datei anwenden.
