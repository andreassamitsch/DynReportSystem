using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace DynReportSystem.Services;

public sealed class SqlDataSourceForm
{
    public string Key { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Authentication { get; set; } = "Windows";
    public string Username { get; set; } = "";
    // Write-only: this value is never included in List() or Get().
    public string Password { get; set; } = "";
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; }
}

public sealed record SqlDataSourceInfo(
    string Key,
    string Server,
    string Database,
    string Authentication,
    string Username,
    bool Encrypt,
    bool TrustServerCertificate,
    bool HasStoredPassword);

internal sealed class StoredSqlDataSource
{
    public string Key { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Authentication { get; set; } = "Windows";
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; }
}

/// <summary>
/// Named SQL connections configured in the portal. Credentials never enter
/// report packages, browser read models, logs or cleartext config files.
/// Passwords are DPAPI machine-protected and the registry file is ACL-restricted.
/// </summary>
public sealed class ManagedSqlDataSources(
    IConfiguration config,
    ILogger<ManagedSqlDataSources> logger)
{
    private static readonly Regex ValidKey =
        new(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,99}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DynReportSystem", "Config", "sql-datasources.json");

    public IReadOnlyList<SqlDataSourceInfo> List() =>
        ReadEntries()
            .Select(ToInfo)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public SqlDataSourceInfo? Get(string key) =>
        ReadEntries().Where(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(ToInfo).FirstOrDefault();

    public string? Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var item = ReadEntries().FirstOrDefault(
            x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return item is null ? null : ConnectionString(item);
    }

    public SqlDataSourceForm ForEditing(string key)
    {
        var entry = ReadEntries().FirstOrDefault(x =>
            x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Datenquelle '{key}' ist unbekannt.");

        return new SqlDataSourceForm
        {
            Key = entry.Key,
            Server = entry.Server,
            Database = entry.Database,
            Authentication = entry.Authentication,
            Username = entry.Username,
            // Never send a stored password back to Blazor client.
            Password = "",
            Encrypt = entry.Encrypt,
            TrustServerCertificate = entry.TrustServerCertificate
        };
    }

    public async Task TestAsync(SqlDataSourceForm form, CancellationToken cancellationToken = default)
    {
        ValidateForm(form);
        // The same connection construction is used by the runtime, so the
        // test covers the exact SQL login/server/database/TLS settings.
        var candidate = Prepare(form, ReadEntries());
        await using var connection = new SqlConnection(ConnectionString(candidate));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT 1", connection)
        {
            CommandTimeout = 5
        };
        _ = await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task SaveAsync(SqlDataSourceForm form, CancellationToken cancellationToken = default)
    {
        ValidateForm(form);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = ReadEntries();
            var updated = Prepare(form, entries);
            var at = entries.FindIndex(x => x.Key.Equals(form.Key, StringComparison.OrdinalIgnoreCase));

            if (at >= 0) entries[at] = updated;
            else entries.Add(updated);

            await WriteAsync(entries, cancellationToken);
            logger.LogInformation("Managed SQL data source {Key} updated.", updated.Key);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = ReadEntries();
            if (entries.RemoveAll(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) > 0)
                await WriteAsync(entries, cancellationToken);
            logger.LogInformation("Managed SQL data source {Key} deleted.", key);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string ConfigurationPath => _path;

    private static SqlDataSourceInfo ToInfo(StoredSqlDataSource item) =>
        new(item.Key, item.Server, item.Database, item.Authentication,
            item.Username, item.Encrypt, item.TrustServerCertificate,
            !string.IsNullOrEmpty(item.ProtectedPassword));

    private static void ValidateForm(SqlDataSourceForm form)
    {
        if (!ValidKey.IsMatch(form.Key.Trim()))
            throw new InvalidDataException("Kennung: 1–100 Zeichen (Buchstaben, Zahlen, _, . oder -).");

        if (string.IsNullOrWhiteSpace(form.Server)
            || form.Server.Length > 250 || form.Server.Any(char.IsControl))
            throw new InvalidDataException("Bitte einen gültigen SQL-Server (maximal 250 Zeichen) eingeben.");

        if (string.IsNullOrWhiteSpace(form.Database)
            || form.Database.Length > 128 || form.Database.Any(char.IsControl))
            throw new InvalidDataException("Bitte eine gültige Datenbank (maximal 128 Zeichen) eingeben.");

        if (form.Authentication is not ("Windows" or "Sql"))
            throw new InvalidDataException("Ungültige Authentifizierungsart.");

        if (form.Authentication == "Sql"
            && (string.IsNullOrWhiteSpace(form.Username) || form.Username.Length > 128))
            throw new InvalidDataException("Für SQL-Anmeldung ist ein Benutzername erforderlich.");

        if (form.Password.Length > 1024)
            throw new InvalidDataException("Das Passwort ist zu lang.");
    }

    private static StoredSqlDataSource Prepare(
        SqlDataSourceForm form,
        List<StoredSqlDataSource> current)
    {
        var previous = current.FirstOrDefault(x =>
            x.Key.Equals(form.Key.Trim(), StringComparison.OrdinalIgnoreCase));
        var password = form.Authentication == "Sql"
            ? !string.IsNullOrEmpty(form.Password)
                ? Protect(form.Password)
                : previous is { Authentication: "Sql" }
                    ? previous.ProtectedPassword
                    : ""
            : "";

        if (form.Authentication == "Sql" && string.IsNullOrEmpty(password))
            throw new InvalidDataException("Bitte ein Passwort für den SQL-Benutzer eingeben.");

        return new StoredSqlDataSource
        {
            Key = form.Key.Trim(),
            Server = form.Server.Trim(),
            Database = form.Database.Trim(),
            Authentication = form.Authentication,
            Username = form.Authentication == "Sql" ? form.Username.Trim() : "",
            ProtectedPassword = password,
            Encrypt = form.Encrypt,
            TrustServerCertificate = form.TrustServerCertificate
        };
    }

    private static string ConnectionString(StoredSqlDataSource item)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = item.Server,
            InitialCatalog = item.Database,
            IntegratedSecurity = item.Authentication == "Windows",
            Encrypt = item.Encrypt,
            TrustServerCertificate = item.TrustServerCertificate,
            ConnectTimeout = 5,
            ApplicationName = "DynReport"
        };

        if (item.Authentication == "Sql")
        {
            builder.UserID = item.Username;
            builder.Password = Unprotect(item.ProtectedPassword);
        }

        return builder.ConnectionString;
    }

    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("DynReportSystem:ManagedSqlDataSources:v1");

    private static string Protect(string value) =>
        Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.LocalMachine));

    private static string Unprotect(string value) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(value), Entropy, DataProtectionScope.LocalMachine));

    private List<StoredSqlDataSource> ReadEntries()
    {
        if (!File.Exists(_path))
            return [];

        // Do not silently fall back to an older registry containing potentially
        // revoked credentials.
        return JsonSerializer.Deserialize<List<StoredSqlDataSource>>(
            File.ReadAllText(_path), JsonOptions)
            ?? throw new InvalidDataException("SQL-Datenquellenverwaltung ist ungültig.");
    }

    private async Task WriteAsync(List<StoredSqlDataSource> entries, CancellationToken token)
    {
        var folder = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(folder);

        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Only encrypted credentials are ever written. The temp and final
            // files explicitly deny inherited broad read permissions.
            await File.WriteAllTextAsync(
                temp, JsonSerializer.Serialize(entries, JsonOptions), token);
            RestrictToServiceAndAdministrators(temp);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static void RestrictToServiceAndAdministrators(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("SQL-Datenquellenverwaltung benötigt Windows DPAPI.");

        var access = new FileSecurity();
        access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sid in new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            WindowsIdentity.GetCurrent().User
                ?? throw new UnauthorizedAccessException("Keine Windows-Dienstidentität.")
        }.Distinct())
        {
            access.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        new FileInfo(path).SetAccessControl(access);
    }
}
