using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

const string ProductName = "DynReport System";
const string Version = "0.1.0";
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

    Directory.CreateDirectory(installDir);
    CopyDirectory(payloadDir, installDir);
    Directory.CreateDirectory(reportsDir);

    if (prodBackup is not null)
    {
        File.WriteAllText(existingProd, prodBackup, new UTF8Encoding(false));
    }
    else
    {
        var example = Path.Combine(installDir, "appsettings.Production.json.example");
        if (File.Exists(example))
            File.Copy(example, existingProd, true);
    }

    if (aclBackup is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(existingAcl)!);
        File.WriteAllText(existingAcl, aclBackup, new UTF8Encoding(false));
    }
    else if (File.Exists(existingAcl))
    {
        var installerUser = WindowsIdentity.GetCurrent().Name;
        var acl = File.ReadAllText(existingAcl)
            .Replace("__INSTALLER_USER__", installerUser, StringComparison.Ordinal);
        File.WriteAllText(existingAcl, acl, new UTF8Encoding(false));
        Console.WriteLine($"Erstadministrator: {installerUser}");
    }

    var setupDir = AppContext.BaseDirectory;
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
if ($LASTEXITCODE -ne 0) { throw 'Anonymous Authentication konnte nicht in ApplicationHost.config konfiguriert werden.' }
& $appcmd set config '{SiteName}' /section:system.webServer/security/authentication/windowsAuthentication /enabled:true /commit:apphost | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Windows Authentication konnte nicht in ApplicationHost.config konfiguriert werden.' }
icacls '{escaped}' /grant 'IIS_IUSRS:(OI)(CI)(RX)' /T /C | Out-Null
Start-WebAppPool -Name '{AppPool}' -ErrorAction SilentlyContinue
Start-Website -Name '{SiteName}' -ErrorAction SilentlyContinue
");
}

void EnsureIisFeatures()
{
    RunPowerShell(@"
if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
  Install-WindowsFeature Web-Server,Web-Windows-Auth,Web-Mgmt-Tools -IncludeManagementTools | Out-Null
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
        File.Copy(file, destination, true);
    }
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
