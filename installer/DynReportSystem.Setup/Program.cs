using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

const string ProductName = "DynReport System";
const string Version = "0.9.0";
const string SiteName = "DynReportSystem";
const string AppPool = "DynReportSystem";
const int DefaultPort = 47131;

try
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.Title = $"{ProductName} Setup {Version}";

    if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
    {
        Uninstall();
        return;
    }

    Install();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\nInstallation fehlgeschlagen: {ex.Message}");
    Console.ResetColor();
    Console.WriteLine(ex);
    Console.WriteLine("\nEnter drücken zum Beenden.");
    Console.ReadLine();
    Environment.ExitCode = 1;
}

void Install()
{
    var installDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "DynReportSystem");

    var reportsDir = Path.Combine(installDir, "Reports");
    var temp = Path.Combine(
        Path.GetTempPath(),
        "DynReportSystemSetup",
        Guid.NewGuid().ToString("N"));

    var payloadZip = Path.Combine(temp, "payload.zip");
    var payloadDir = Path.Combine(temp, "payload");

    Directory.CreateDirectory(temp);
    Directory.CreateDirectory(payloadDir);

    Header("Installation");
    Console.WriteLine($"Ziel: {installDir}");
    Console.WriteLine($"IIS-Site: {SiteName} · Port {DefaultPort}");

    var existingProd = Path.Combine(installDir, "appsettings.Production.json");
    var existingAcl = Path.Combine(installDir, "config", "permissions.json");
    var installedVersion = GetInstalledVersion();

    string? prodBackup = File.Exists(existingProd) ? File.ReadAllText(existingProd) : null;
    string? aclBackup = File.Exists(existingAcl) ? File.ReadAllText(existingAcl) : null;

    using (var resource = Assembly.GetExecutingAssembly()
               .GetManifestResourceStream("DynReportSystem.Payload.zip")
           ?? throw new InvalidOperationException("Installations-Payload fehlt."))
    using (var file = File.Create(payloadZip))
    {
        resource.CopyTo(file);
    }

    ZipFile.ExtractToDirectory(payloadZip, payloadDir, true);

    // Keep the existing portal online while the embedded payload is unpacked.
    // Only stop DynReport immediately before replacing files.
    StopExistingIisForUpdate();

    Directory.CreateDirectory(installDir);
    CopyDirectory(payloadDir, installDir);
    Directory.CreateDirectory(reportsDir);

    var exampleConfig = Path.Combine(installDir, "appsettings.Production.json.example");

    if (prodBackup is not null)
    {
        var backupPath = existingProd + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.WriteAllText(backupPath, prodBackup, new UTF8Encoding(false));

        var merged = File.Exists(exampleConfig)
            ? MergeProductionConfiguration(
                prodBackup,
                File.ReadAllText(exampleConfig),
                installedVersion)
            : prodBackup;

        File.WriteAllText(existingProd, merged, new UTF8Encoding(false));
        Console.WriteLine($"Produktionskonfiguration gesichert: {backupPath}");
        Console.WriteLine("Neue Konfigurationsschlüssel wurden ergänzt; vorhandene Werte und ConnectionStrings blieben erhalten.");
    }
    else if (File.Exists(exampleConfig))
    {
        File.Copy(exampleConfig, existingProd, true);
    }

    if (aclBackup is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(existingAcl)!);
        File.WriteAllText(existingAcl, aclBackup, new UTF8Encoding(false));
    }
    else if (File.Exists(existingAcl))
    {
        var installerUser = WindowsIdentity.GetCurrent().Name;
        var jsonEscapedInstallerUser = JsonSerializer.Serialize(installerUser)[1..^1];
        var acl = File.ReadAllText(existingAcl)
            .Replace("__INSTALLER_USER__", jsonEscapedInstallerUser, StringComparison.Ordinal);
        File.WriteAllText(existingAcl, acl, new UTF8Encoding(false));
        Console.WriteLine($"Erstadministrator: {installerUser}");
    }

    var setupDir = AppContext.BaseDirectory;

    var migrationBundle = Path.Combine(setupDir, "MigrationBundle.zip");
    if (File.Exists(migrationBundle))
    {
        Console.WriteLine("SSRS-Migrationspaket wird installiert …");
        ZipFile.ExtractToDirectory(migrationBundle, installDir, true);
        EnsureMigrationAdministrator(installDir);
        Console.WriteLine("SSRS-Katalog, RDLs und Berechtigungen wurden übernommen.");
    }

    var installedRdl = 0;

    foreach (var rdl in Directory.EnumerateFiles(setupDir, "*.rdl", SearchOption.TopDirectoryOnly))
    {
        var destination = Path.Combine(reportsDir, Path.GetFileName(rdl));
        File.Copy(rdl, destination, true);
        installedRdl++;
        Console.WriteLine($"RDL installiert: {Path.GetFileName(rdl)}");
    }

    if (installedRdl == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Hinweis: Keine RDL neben dem Setup gefunden.");
        Console.WriteLine($"Berichte können später nach {reportsDir} kopiert werden.");
        Console.ResetColor();
    }

    var self = Environment.ProcessPath;
    if (!string.IsNullOrWhiteSpace(self) && File.Exists(self))
        File.Copy(self, Path.Combine(installDir, "DynReportSystem-Setup.exe"), true);

    EnsureIisFeatures();

    if (!HasAspNetCoreModule())
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\nVORAUSSETZUNG FEHLT: ASP.NET Core Module v2 wurde nicht gefunden.");
        Console.WriteLine("Installiere auf APP-01 zuerst das aktuelle .NET 10 Hosting Bundle von Microsoft.");
        Console.WriteLine("Danach WAS/W3SVC neu starten und dieses Setup erneut ausführen.");
        Console.ResetColor();

        throw new InvalidOperationException(
            "ASP.NET Core Module v2 fehlt. IIS-Konfiguration wurde daher nicht fortgesetzt.");
    }

    ConfigureIis(installDir);
    RegisterUninstall(installDir);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("\nInstallation abgeschlossen.");
    Console.ResetColor();

    Console.WriteLine($"Test-URL: http://localhost:{DefaultPort}/");
    Console.WriteLine($"SQL-Konfiguration: {existingProd}");
    Console.WriteLine($"Berechtigungen: {existingAcl}");
    Console.WriteLine($"Berichte: {reportsDir}");
    Console.WriteLine($"DynReport-Pakete: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DynReportSystem", "Packages")}");
    Console.WriteLine($"Revisionen: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DynReportSystem", "Revisions")}");

    try
    {
        Process.Start(new ProcessStartInfo("explorer.exe", installDir)
        {
            UseShellExecute = true
        });
    }
    catch { }

    TryDeleteDirectory(temp);

    Console.WriteLine("\nEnter drücken zum Beenden.");
    Console.ReadLine();
}

void Uninstall()
{
    var installDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "DynReportSystem");

    Header("Deinstallation");

    RunPowerShell($@"
Import-Module WebAdministration -ErrorAction SilentlyContinue
if (Test-Path 'IIS:\Sites\{SiteName}') {{ Remove-Website -Name '{SiteName}' }}
if (Test-Path 'IIS:\AppPools\{AppPool}') {{ Remove-WebAppPool -Name '{AppPool}' }}
");

    try
    {
        Registry.LocalMachine.DeleteSubKeyTree(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DynReportSystem",
            false);
    }
    catch { }

    Console.WriteLine("IIS-Site und AppPool wurden entfernt.");
    Console.WriteLine("Konfiguration und Reports bleiben aus Sicherheitsgründen erhalten.");
    Console.WriteLine($"Ordner bei Bedarf manuell löschen: {installDir}");
}

void StopExistingIisForUpdate()
{
    // IIS/ANCM shutdown is asynchronous. Support both hosting models while
    // upgrading: older releases ran in w3wp (in-process), 0.7.9+ runs the
    // DynReportSystem.exe Kestrel child process behind IIS (out-of-process).
    RunPowerShell($@"
$ProgressPreference = 'SilentlyContinue'
Import-Module WebAdministration -ErrorAction SilentlyContinue
$dynExe = Join-Path $env:ProgramFiles 'DynReportSystem\DynReportSystem.exe'

if (Test-Path 'IIS:\Sites\{SiteName}') {{
  Stop-Website -Name '{SiteName}' -ErrorAction SilentlyContinue
}}

if (Test-Path 'IIS:\AppPools\{AppPool}') {{
  Stop-WebAppPool -Name '{AppPool}' -ErrorAction SilentlyContinue
}}

function Get-DynReportWorkers {{
  $iisWorkers = @(Get-CimInstance Win32_Process -Filter ""Name='w3wp.exe'"" -ErrorAction SilentlyContinue |
    Where-Object {{
      $_.CommandLine -match '-ap\s+""?{AppPool}""?'
    }})

  $kestrelWorkers = @(Get-CimInstance Win32_Process -Filter ""Name='DynReportSystem.exe'"" -ErrorAction SilentlyContinue |
    Where-Object {{
      $_.ExecutablePath -and
      $_.ExecutablePath.Equals($dynExe, [System.StringComparison]::OrdinalIgnoreCase)
    }})

  @($iisWorkers + $kestrelWorkers)
}}

for ($i = 0; $i -lt 40; $i++) {{
  $poolStopped = $true
  if (Test-Path 'IIS:\AppPools\{AppPool}') {{
    try {{
      $poolStopped = ((Get-WebAppPoolState -Name '{AppPool}').Value -eq 'Stopped')
    }} catch {{
      $poolStopped = $true
    }}
  }}

  $workers = Get-DynReportWorkers
  if ($poolStopped -and $workers.Count -eq 0) {{
    break
  }}

  Start-Sleep -Milliseconds 250
}}

$workers = Get-DynReportWorkers
foreach ($worker in $workers) {{
  Stop-Process -Id $worker.ProcessId -Force -ErrorAction SilentlyContinue
}}

for ($i = 0; $i -lt 20; $i++) {{
  if ((Get-DynReportWorkers).Count -eq 0) {{ break }}
  Start-Sleep -Milliseconds 250
}}
", throwOnError: false);

    Thread.Sleep(500);
}

System.Version? GetInstalledVersion()
{
    try
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DynReportSystem");

        var value = key?.GetValue("DisplayVersion")?.ToString();
        return System.Version.TryParse(value, out var version)
            ? version
            : null;
    }
    catch
    {
        return null;
    }
}

string MergeProductionConfiguration(
    string existingJson,
    string templateJson,
    System.Version? installedVersion)
{
    var existing = JsonNode.Parse(existingJson) as JsonObject
        ?? throw new InvalidDataException("Bestehende appsettings.Production.json ist kein JSON-Objekt.");

    var template = JsonNode.Parse(templateJson) as JsonObject
        ?? throw new InvalidDataException("appsettings.Production.json.example ist kein JSON-Objekt.");

    MergeMissingConfiguration(existing, template);

    // 0.8.0 closes the metadata publish path after the SQL revision catalog has
    // been validated. Apply this once when upgrading a pre-0.8 installation
    // that already contains a real metadata connection. Future upgrades preserve
    // an administrator's explicit setting.
    var upgradingFromPre080 =
        installedVersion is null || installedVersion < new System.Version(0, 8, 0);

    if (upgradingFromPre080 && HasRealMetadataConnection(existing))
    {
        var metadata = EnsureObject(existing, "Metadata");
        metadata["FailPublishWhenUnavailable"] = true;
        Console.WriteLine(
            "SQL-Revisionskatalog erkannt: Metadata:FailPublishWhenUnavailable wurde auf true gehärtet.");
    }

    return existing.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true
    });
}

void MergeMissingConfiguration(JsonObject target, JsonObject defaults)
{
    foreach (var property in defaults)
    {
        // Never introduce environment-specific connection strings from the
        // shipped example into an existing production installation.
        if (property.Key.Equals("ConnectionString", StringComparison.OrdinalIgnoreCase))
            continue;

        if (!target.TryGetPropertyValue(property.Key, out var current) || current is null)
        {
            if (property.Value is JsonObject defaultObject)
            {
                var newObject = new JsonObject();
                target[property.Key] = newObject;
                MergeMissingConfiguration(newObject, defaultObject);
            }
            else
            {
                target[property.Key] = property.Value?.DeepClone();
            }

            continue;
        }

        if (current is JsonObject currentObject && property.Value is JsonObject templateObject)
            MergeMissingConfiguration(currentObject, templateObject);
    }
}

bool HasRealMetadataConnection(JsonObject root)
{
    var metadataConnection =
        root["Metadata"]?["ConnectionString"]?.GetValue<string>();

    if (IsRealConnectionString(metadataConnection))
        return true;

    var dataSourceConnection =
        root["DataSources"]?["DynReportMetadata"]?["ConnectionString"]?.GetValue<string>();

    return IsRealConnectionString(dataSourceConnection);
}

bool IsRealConnectionString(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return false;

    return !value.Contains("SQL_SERVER", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("DATABASE_NAME", StringComparison.OrdinalIgnoreCase);
}

JsonObject EnsureObject(JsonObject parent, string propertyName)
{
    if (parent[propertyName] is JsonObject existing)
        return existing;

    var created = new JsonObject();
    parent[propertyName] = created;
    return created;
}

void EnsureMigrationAdministrator(string installDir)
{
    var path = Path.Combine(installDir, "migration", "permissions.json");
    if (!File.Exists(path))
        return;

    var installerUser = WindowsIdentity.GetCurrent().Name;
    var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
    var folders = node?["Folders"] as JsonArray;

    if (folders is null)
        return;

    foreach (var folderNode in folders)
    {
        if (folderNode is not JsonObject folder)
            continue;

        if (folder["ParentId"] is not null)
            continue;

        var grants = folder["Grants"] as JsonArray;
        if (grants is null)
        {
            grants = new JsonArray();
            folder["Grants"] = grants;
        }

        var exists = grants
            .OfType<JsonObject>()
            .Any(g => string.Equals(
                g["Principal"]?.GetValue<string>(),
                installerUser,
                StringComparison.OrdinalIgnoreCase));

        if (exists)
            continue;

        grants.Add(new JsonObject
        {
            ["PrincipalType"] = "User",
            ["Principal"] = installerUser,
            ["Permissions"] = new JsonArray("View", "Run", "Edit", "Publish", "Manage")
        });
    }

    File.WriteAllText(
        path,
        node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
        new UTF8Encoding(false));

    Console.WriteLine($"Migrationsadministrator: {installerUser}");
}

void ConfigureIis(string installDir)
{
    var escaped = installDir.Replace("'", "''");

    RunPowerShell($@"
Import-Module WebAdministration -ErrorAction Stop
if (-not (Test-Path 'IIS:\AppPools\{AppPool}')) {{ New-WebAppPool -Name '{AppPool}' | Out-Null }}
Set-ItemProperty 'IIS:\AppPools\{AppPool}' -Name managedRuntimeVersion -Value ''
Set-ItemProperty 'IIS:\AppPools\{AppPool}' -Name processModel.identityType -Value 4
if (Test-Path 'IIS:\Sites\{SiteName}') {{
  Set-ItemProperty 'IIS:\Sites\{SiteName}' -Name physicalPath -Value '{escaped}'
}} else {{
  New-Website -Name '{SiteName}' -PhysicalPath '{escaped}' -Port {DefaultPort} -ApplicationPool '{AppPool}' | Out-Null
}}
$appcmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'
& $appcmd set config '{SiteName}' /section:system.webServer/security/authentication/anonymousAuthentication /enabled:false /commit:apphost | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'Anonymous Authentication konnte nicht in ApplicationHost.config konfiguriert werden.' }}
& $appcmd set config '{SiteName}' /section:system.webServer/security/authentication/windowsAuthentication /enabled:true /commit:apphost | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'Windows Authentication konnte nicht in ApplicationHost.config konfiguriert werden.' }}
& $appcmd set config '{SiteName}' /section:system.webServer/webSocket /enabled:true /commit:apphost | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'WebSocket Protocol konnte für DynReport nicht aktiviert werden.' }}
icacls '{escaped}' /grant 'IIS_IUSRS:(OI)(CI)(RX)' /T /C | Out-Null

$dynRoot = Join-Path $env:ProgramData 'DynReportSystem'
$packageDir = Join-Path $dynRoot 'Packages'
$revisionDir = Join-Path $dynRoot 'Revisions'
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
New-Item -ItemType Directory -Force -Path $revisionDir | Out-Null
icacls $packageDir /grant 'IIS AppPool\{AppPool}:(OI)(CI)(M)' /T /C | Out-Null
icacls $revisionDir /grant 'IIS AppPool\{AppPool}:(OI)(CI)(M)' /T /C | Out-Null

Start-WebAppPool -Name '{AppPool}' -ErrorAction SilentlyContinue
Start-Website -Name '{SiteName}' -ErrorAction SilentlyContinue
");
}

void EnsureIisFeatures()
{
    RunPowerShell(@"
if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
  Install-WindowsFeature Web-Server,Web-Windows-Auth,Web-WebSockets,Web-Mgmt-Tools -IncludeManagementTools | Out-Null
}
", throwOnError: false);
}

bool HasAspNetCoreModule()
{
    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    return File.Exists(Path.Combine(
        programFiles,
        "IIS",
        "Asp.Net Core Module",
        "V2",
        "aspnetcorev2.dll"));
}

void RegisterUninstall(string installDir)
{
    using var key = Registry.LocalMachine.CreateSubKey(
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DynReportSystem");

    key?.SetValue("DisplayName", ProductName);
    key?.SetValue("DisplayVersion", Version);
    key?.SetValue("Publisher", "Fuchshofer");
    key?.SetValue("InstallLocation", installDir);
    key?.SetValue(
        "UninstallString",
        $"\"{Path.Combine(installDir, "DynReportSystem-Setup.exe")}\" --uninstall");
    key?.SetValue("NoModify", 1, RegistryValueKind.DWord);
    key?.SetValue("NoRepair", 1, RegistryValueKind.DWord);
}

void RunPowerShell(string script, bool throwOnError = true)
{
    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = "powershell.exe",
        Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("PowerShell konnte nicht gestartet werden.");

    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();

    process.WaitForExit();

    if (!string.IsNullOrWhiteSpace(output))
        Console.WriteLine(output.Trim());

    if (process.ExitCode != 0)
    {
        if (throwOnError)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"PowerShell ExitCode {process.ExitCode}"
                    : error.Trim());

        if (!string.IsNullOrWhiteSpace(error))
            Console.WriteLine($"Hinweis: {error.Trim()}");
    }
}

void CopyDirectory(string source, string target)
{
    foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(
            Path.Combine(target, Path.GetRelativePath(source, directory)));
    }

    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(source, file);
        var destination = Path.Combine(target, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        CopyFileWithRetry(file, destination);
    }
}

void CopyFileWithRetry(string source, string destination)
{
    const int attempts = 30;

    for (var attempt = 1; attempt <= attempts; attempt++)
    {
        try
        {
            File.Copy(source, destination, true);
            return;
        }
        catch (IOException) when (attempt < attempts)
        {
            if (attempt == 1)
                Console.WriteLine($"Datei noch in Verwendung, warte auf Freigabe: {Path.GetFileName(destination)}");

            Thread.Sleep(500);
        }
        catch (UnauthorizedAccessException) when (attempt < attempts)
        {
            Thread.Sleep(500);
        }
    }

    // Let the final exception keep the original file path/details.
    File.Copy(source, destination, true);
}

void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);
    }
    catch { }
}

void Header(string action)
{
    Console.WriteLine("============================================================");
    Console.WriteLine($" {ProductName} {Version} · {action}");
    Console.WriteLine(" APP-01 / Windows Server / IIS / Windows SSO");
    Console.WriteLine("============================================================\n");
}
