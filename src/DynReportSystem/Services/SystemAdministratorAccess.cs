using System.Security.Claims;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynReportSystem.Services;

/// <summary>
/// Server-defined system administrators, deliberately independent of report
/// folder permissions. There is no HTTP/Blazor mutation path for this file.
/// Provision principals by SID locally on APP-01 using the elevated tool.
/// </summary>
public sealed class SystemAdministratorAccess(
    ILogger<SystemAdministratorAccess> logger)
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DynReportSystem", "Security", "system-administrators.json");

    private static readonly Regex SidFormat = new(
        @"^S-1-(?:\d+-){1,14}\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public bool IsSystemAdministrator(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true
            || principal.Identity is not WindowsIdentity identity
            || !OperatingSystem.IsWindows())
            return false;

        try
        {
            var sids = ReadAdministrators();
            if (sids.Count == 0)
                return false;

            var windowsUser = new WindowsPrincipal(identity);
            return sids.Any(sid => identity.User?.Equals(sid) == true
                                   || windowsUser.IsInRole(sid));
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or InvalidDataException
                                   or ArgumentException)
        {
            // Fail closed on missing/invalid or unreadable ACL configuration.
            logger.LogError(ex, "System administrator ACL cannot be read.");
            return false;
        }
    }

    public string ConfigurationPath => _path;

    private IReadOnlyList<SecurityIdentifier> ReadAdministrators()
    {
        if (!File.Exists(_path))
            return [];

        using var document = JsonDocument.Parse(File.ReadAllText(_path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("SchemaVersion", out var schema)
            || schema.GetInt32() != 1
            || !root.TryGetProperty("Administrators", out var admins)
            || admins.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Systemadministrator-Konfiguration hat ein ungültiges Schema.");
        }

        var sids = new List<SecurityIdentifier>();
        foreach (var entry in admins.EnumerateArray())
        {
            if (!entry.TryGetProperty("Sid", out var sidValue)
                || sidValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Systemadministrator ohne SID.");

            var value = sidValue.GetString() ?? "";
            if (!SidFormat.IsMatch(value))
                throw new InvalidDataException("Ungültige Systemadministrator-SID.");

            sids.Add(new SecurityIdentifier(value));
        }

        return sids;
    }
}
