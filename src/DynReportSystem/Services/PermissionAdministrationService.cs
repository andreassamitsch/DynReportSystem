using System.Security.Claims;
using System.Text.Json;

namespace DynReportSystem.Services;

public sealed record PermissionTargetState(
    string TargetType,
    string Id,
    string Title,
    IReadOnlyList<ReportGrant> DirectGrants,
    IReadOnlyList<ReportGrant> InheritedGrants,
    bool HasOverride);

public sealed class PermissionAdministrationService(
    FolderAccess access,
    DynReportMetadataStore metadata,
    ILogger<PermissionAdministrationService> logger)
{
    public static readonly string[] SupportedPermissions =
    [
        "View",
        "Run",
        "EditLayout",
        "Edit",
        "Publish",
        "Manage"
    ];

    public static readonly string[] SupportedPrincipalTypes =
    [
        "User",
        "Group",
        "WindowsPrincipal"
    ];

    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string StoragePath => access.PermissionsOverridePath;

    public PermissionTargetState GetFolderState(string folderId)
    {
        var catalog = access.Catalog;
        var folder = catalog.Folders.FirstOrDefault(x =>
            x.Id.Equals(folderId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Ordner '{folderId}' wurde nicht gefunden.");

        var overrides = ReadOverrides();

        return new PermissionTargetState(
            "Folder",
            folder.Id,
            folder.Title,
            Clone(folder.Grants),
            access.InheritedFolderGrants(folder.Id),
            overrides.Folders.Any(x =>
                x.Id.Equals(folder.Id, StringComparison.OrdinalIgnoreCase)));
    }

    public PermissionTargetState GetReportState(string reportId)
    {
        var catalog = access.Catalog;
        var report = catalog.Reports.FirstOrDefault(x =>
            x.Id.Equals(reportId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Bericht '{reportId}' wurde nicht gefunden.");

        var overrides = ReadOverrides();

        return new PermissionTargetState(
            "Report",
            report.Id,
            report.Title,
            Clone(report.Grants),
            access.InheritedReportGrants(report.Id),
            overrides.Reports.Any(x =>
                x.Id.Equals(report.Id, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task SaveFolderAsync(
        ClaimsPrincipal actor,
        string folderId,
        IEnumerable<ReportGrant> grants,
        CancellationToken cancellationToken = default)
    {
        if (!access.CanFolder(actor, folderId, "Manage"))
            throw new UnauthorizedAccessException(
                "Für diesen Ordner ist die Berechtigung 'Manage' erforderlich.");

        await SaveAsync(
            actor,
            "Folder",
            folderId,
            grants,
            cancellationToken);
    }

    public async Task SaveReportAsync(
        ClaimsPrincipal actor,
        string reportId,
        IEnumerable<ReportGrant> grants,
        CancellationToken cancellationToken = default)
    {
        if (!access.Can(actor, reportId, "Manage"))
            throw new UnauthorizedAccessException(
                "Für diesen Bericht ist die Berechtigung 'Manage' erforderlich.");

        await SaveAsync(
            actor,
            "Report",
            reportId,
            grants,
            cancellationToken);
    }

    public async Task ResetFolderAsync(
        ClaimsPrincipal actor,
        string folderId,
        CancellationToken cancellationToken = default)
    {
        if (!access.CanFolder(actor, folderId, "Manage"))
            throw new UnauthorizedAccessException(
                "Für diesen Ordner ist die Berechtigung 'Manage' erforderlich.");

        await ResetAsync(actor, "Folder", folderId, cancellationToken);
    }

    public async Task ResetReportAsync(
        ClaimsPrincipal actor,
        string reportId,
        CancellationToken cancellationToken = default)
    {
        if (!access.Can(actor, reportId, "Manage"))
            throw new UnauthorizedAccessException(
                "Für diesen Bericht ist die Berechtigung 'Manage' erforderlich.");

        await ResetAsync(actor, "Report", reportId, cancellationToken);
    }

    private async Task SaveAsync(
        ClaimsPrincipal actor,
        string targetType,
        string targetId,
        IEnumerable<ReportGrant> grants,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(grants);

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var document = ReadOverrides();
            var target = TargetList(document, targetType);
            var existing = target.FirstOrDefault(x =>
                x.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                existing = new PermissionTargetOverride { Id = targetId };
                target.Add(existing);
            }

            existing.Grants = normalized;
            await WriteAtomicAsync(document, cancellationToken);
            access.Invalidate();

            await AuditAsync(
                actor,
                "permissions.override.save",
                targetType,
                targetId,
                normalized,
                cancellationToken);

            logger.LogInformation(
                "Permissions for {TargetType} {TargetId} saved by {User}",
                targetType,
                targetId,
                actor.Identity?.Name ?? "unknown");
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task ResetAsync(
        ClaimsPrincipal actor,
        string targetType,
        string targetId,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var document = ReadOverrides();
            var target = TargetList(document, targetType);
            target.RemoveAll(x =>
                x.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase));

            await WriteAtomicAsync(document, cancellationToken);
            access.Invalidate();

            await AuditAsync(
                actor,
                "permissions.override.reset",
                targetType,
                targetId,
                [],
                cancellationToken);

            logger.LogInformation(
                "Permission override for {TargetType} {TargetId} reset by {User}",
                targetType,
                targetId,
                actor.Identity?.Name ?? "unknown");
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private PermissionOverrideDocument ReadOverrides()
    {
        var path = StoragePath;
        if (!File.Exists(path))
            return new PermissionOverrideDocument();

        try
        {
            return JsonSerializer.Deserialize<PermissionOverrideDocument>(
                File.ReadAllText(path),
                _json)
                ?? new PermissionOverrideDocument();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Die Portal-Berechtigungsdatei '{path}' ist ungültig.",
                ex);
        }
    }

    private async Task WriteAtomicAsync(
        PermissionOverrideDocument document,
        CancellationToken cancellationToken)
    {
        var path = StoragePath;
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "Speicherpfad für Portal-Berechtigungen ist ungültig.");

        Directory.CreateDirectory(directory);

        document.Folders = document.Folders
            .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        document.Reports = document.Reports
            .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var temp = path + ".tmp";
        var backup = path + ".bak";
        var json = JsonSerializer.Serialize(document, _json);

        try
        {
            await File.WriteAllTextAsync(temp, json, cancellationToken);

            if (File.Exists(path))
                File.Copy(path, backup, overwrite: true);

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static List<PermissionTargetOverride> TargetList(
        PermissionOverrideDocument document,
        string targetType) =>
        targetType.Equals("Folder", StringComparison.OrdinalIgnoreCase)
            ? document.Folders
            : document.Reports;

    private static List<ReportGrant> Normalize(
        IEnumerable<ReportGrant> grants)
    {
        var result = new List<ReportGrant>();

        foreach (var group in grants
            .Where(grant => !string.IsNullOrWhiteSpace(grant.Principal))
            .GroupBy(
                grant => (
                    Type: NormalizePrincipalType(grant.PrincipalType),
                    Principal: grant.Principal.Trim()),
                new PrincipalKeyComparer()))
        {
            var permissions = group
                .SelectMany(x => x.Permissions)
                .Where(permission =>
                    SupportedPermissions.Contains(
                        permission,
                        StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    permission => Array.FindIndex(
                        SupportedPermissions,
                        x => x.Equals(
                            permission,
                            StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (permissions.Count == 0)
                continue;

            result.Add(new ReportGrant
            {
                PrincipalType = group.Key.Type,
                Principal = group.Key.Principal,
                Permissions = permissions
            });
        }

        return result
            .OrderBy(x => x.PrincipalType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Principal, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizePrincipalType(string? value)
    {
        var type = (value ?? "").Trim();

        return SupportedPrincipalTypes.FirstOrDefault(x =>
            x.Equals(type, StringComparison.OrdinalIgnoreCase))
            ?? "Group";
    }

    private async Task AuditAsync(
        ClaimsPrincipal actor,
        string eventType,
        string targetType,
        string targetId,
        IReadOnlyList<ReportGrant> grants,
        CancellationToken cancellationToken)
    {
        await metadata.WriteAuditAsync(
            new DynAuditEvent(
                Guid.NewGuid(),
                eventType,
                "success",
                actor.Identity?.Name,
                targetType.Equals("Report", StringComparison.OrdinalIgnoreCase)
                    ? targetId
                    : null,
                DetailsJson: DynAuditEvent.Details(new
                {
                    TargetType = targetType,
                    TargetId = targetId,
                    GrantCount = grants.Count,
                    Grants = grants.Select(grant => new
                    {
                        grant.PrincipalType,
                        grant.Principal,
                        grant.Permissions
                    })
                })),
            cancellationToken);
    }

    private static List<ReportGrant> Clone(
        IEnumerable<ReportGrant> grants) =>
        grants.Select(grant => new ReportGrant
        {
            PrincipalType = grant.PrincipalType,
            Principal = grant.Principal,
            Permissions = grant.Permissions.ToList()
        }).ToList();

    private sealed class PrincipalKeyComparer
        : IEqualityComparer<(string Type, string Principal)>
    {
        public bool Equals(
            (string Type, string Principal) x,
            (string Type, string Principal) y) =>
            x.Type.Equals(y.Type, StringComparison.OrdinalIgnoreCase)
            && x.Principal.Equals(y.Principal, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Type, string Principal) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Type),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Principal));
    }
}
