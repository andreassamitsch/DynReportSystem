using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DynReportSystem.Services;

public sealed class PlatformCatalog
{
    public List<ReportFolder> Folders { get; set; } = [];
    public List<ReportItem> Reports { get; set; } = [];
}

public sealed class ReportFolder
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? ParentId { get; set; }
    public List<ReportGrant> Grants { get; set; } = [];
}

public sealed class ReportItem
{
    public string Id { get; set; } = "";
    public string FolderId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public List<string> Datasets { get; set; } = [];
    public List<ReportGrant> Grants { get; set; } = [];
}

public sealed class ReportGrant
{
    public string PrincipalType { get; set; } = "Group";
    public string Principal { get; set; } = "";
    public List<string> Permissions { get; set; } = [];
}

public sealed class PermissionOverrideDocument
{
    public List<PermissionTargetOverride> Folders { get; set; } = [];
    public List<PermissionTargetOverride> Reports { get; set; } = [];
}

public sealed class PermissionTargetOverride
{
    public string Id { get; set; } = "";
    public List<ReportGrant> Grants { get; set; } = [];
}

/// <summary>
/// Explicit allow only. Rights are inherited from ancestor folders.
/// The local config can be extended by an imported SSRS migration ACL.
/// </summary>
public sealed class FolderAccess(
    IWebHostEnvironment host,
    DynReportPackageStore packageStore)
{
    private readonly string _path = Path.Combine(host.ContentRootPath, "config", "permissions.json");
    private readonly string _migrationPath = Path.Combine(host.ContentRootPath, "migration", "permissions.json");
    private readonly string _overridePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DynReportSystem",
        "Config",
        "permissions-overrides.json");
    private readonly object _lock = new();
    private PlatformCatalog? _catalog;
    private DateTime _mtime;
    private DateTime _migrationMtime;
    private DateTime _overrideMtime;
    private long _packageGeneration = -1;
    private Dictionary<string, ReportItem> _reportsById =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ReportFolder> _foldersById =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _principalMatchCache =
        new(StringComparer.OrdinalIgnoreCase);

    public PlatformCatalog Catalog
    {
        get
        {
            var time = File.GetLastWriteTimeUtc(_path);
            if (time == DateTime.MinValue)
                throw new FileNotFoundException("ACL-Konfiguration fehlt.", _path);

            var migrationTime = File.Exists(_migrationPath)
                ? File.GetLastWriteTimeUtc(_migrationPath)
                : DateTime.MinValue;

            var overrideTime = File.Exists(_overridePath)
                ? File.GetLastWriteTimeUtc(_overridePath)
                : DateTime.MinValue;
            var packageGeneration = packageStore.Generation;

            lock (_lock)
            {
                if (_catalog is not null
                    && time == _mtime
                    && migrationTime == _migrationMtime
                    && overrideTime == _overrideMtime
                    && packageGeneration == _packageGeneration)
                    return _catalog;
            }

            // Only enumerate/read packages when something actually changed.
            // Access.Can() is called hundreds of times during the first portal
            // render, so packageStore.List() must not sit on its hot path.
            var packages = packageStore.List();
            packageGeneration = packageStore.Generation;

            lock (_lock)
            {
                if (_catalog is not null
                    && time == _mtime
                    && migrationTime == _migrationMtime
                    && overrideTime == _overrideMtime
                    && packageGeneration == _packageGeneration)
                    return _catalog;

                var config = Read(_path);

                if (File.Exists(_migrationPath))
                    Merge(config, Read(_migrationPath));

                MergePackages(config, packages);

                if (File.Exists(_overridePath))
                    ApplyOverrides(config, ReadOverrides(_overridePath));

                Validate(config);

                _catalog = config;
                _reportsById = config.Reports.ToDictionary(
                    x => x.Id,
                    StringComparer.OrdinalIgnoreCase);
                _foldersById = config.Folders.ToDictionary(
                    x => x.Id,
                    StringComparer.OrdinalIgnoreCase);
                _mtime = time;
                _migrationMtime = migrationTime;
                _overrideMtime = overrideTime;
                _packageGeneration = packageGeneration;
                return config;
            }
        }
    }

    public bool Can(ClaimsPrincipal user, string reportId, string permission)
    {
        if (user.Identity?.IsAuthenticated != true)
            return false;

        _ = Catalog;

        if (!_reportsById.TryGetValue(reportId, out var report))
            return false;

        if (Allowed(report.Grants, user, permission))
            return true;

        var folderId = report.FolderId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!string.IsNullOrWhiteSpace(folderId) && visited.Add(folderId))
        {
            if (!_foldersById.TryGetValue(folderId, out var folder))
                return false;

            if (Allowed(folder.Grants, user, permission))
                return true;

            folderId = folder.ParentId;
        }

        return false;
    }

    public bool CanAll(
        ClaimsPrincipal user,
        string reportId,
        params string[] permissions)
    {
        if (user.Identity?.IsAuthenticated != true || permissions.Length == 0)
            return false;

        _ = Catalog;

        if (!_reportsById.TryGetValue(reportId, out var report))
            return false;

        var missing = permissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ApplyAllowed(report.Grants, user, missing);

        var folderId = report.FolderId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (missing.Count > 0
               && !string.IsNullOrWhiteSpace(folderId)
               && visited.Add(folderId))
        {
            if (!_foldersById.TryGetValue(folderId, out var folder))
                break;

            ApplyAllowed(folder.Grants, user, missing);
            folderId = folder.ParentId;
        }

        return missing.Count == 0;
    }

    private void ApplyAllowed(
        IEnumerable<ReportGrant> grants,
        ClaimsPrincipal user,
        HashSet<string> missing)
    {
        foreach (var grant in grants)
        {
            if (missing.Count == 0)
                return;

            var relevant = grant.Permissions
                .Where(permission => missing.Contains(permission))
                .ToArray();

            if (relevant.Length == 0 || !PrincipalMatches(grant, user))
                continue;

            foreach (var permission in relevant)
                missing.Remove(permission);
        }
    }

    public bool CanRunDataset(ClaimsPrincipal user, string dataset) =>
        Catalog.Reports.Any(r =>
            r.Datasets.Contains(dataset, StringComparer.OrdinalIgnoreCase)
            && Can(user, r.Id, "View")
            && Can(user, r.Id, "Run"));

    public string PermissionsOverridePath => _overridePath;

    public bool CanFolder(
        ClaimsPrincipal user,
        string folderId,
        string permission)
    {
        if (user.Identity?.IsAuthenticated != true)
            return false;

        _ = Catalog;

        var currentId = folderId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!string.IsNullOrWhiteSpace(currentId) && visited.Add(currentId))
        {
            if (!_foldersById.TryGetValue(currentId, out var folder))
                return false;

            if (Allowed(folder.Grants, user, permission))
                return true;

            currentId = folder.ParentId ?? "";
        }

        return false;
    }

    public bool CanManageAny(ClaimsPrincipal user) =>
        Catalog.Reports.Any(report => Can(user, report.Id, "Manage"))
        || Catalog.Folders.Any(folder => CanFolder(user, folder.Id, "Manage"));

    public IReadOnlyList<ReportGrant> InheritedFolderGrants(string folderId)
    {
        _ = Catalog;

        if (!_foldersById.TryGetValue(folderId, out var folder))
            return [];

        var result = new List<ReportGrant>();
        var currentId = folder.ParentId ?? "";
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!string.IsNullOrWhiteSpace(currentId) && visited.Add(currentId))
        {
            if (!_foldersById.TryGetValue(currentId, out var parent))
                break;

            result.AddRange(CloneGrants(parent.Grants));
            currentId = parent.ParentId ?? "";
        }

        return result;
    }

    public IReadOnlyList<ReportGrant> InheritedReportGrants(string reportId)
    {
        _ = Catalog;

        if (!_reportsById.TryGetValue(reportId, out var report))
            return [];

        var result = new List<ReportGrant>();
        var currentId = report.FolderId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!string.IsNullOrWhiteSpace(currentId) && visited.Add(currentId))
        {
            if (!_foldersById.TryGetValue(currentId, out var folder))
                break;

            result.AddRange(CloneGrants(folder.Grants));
            currentId = folder.ParentId ?? "";
        }

        return result;
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _catalog = null;
            _reportsById.Clear();
            _foldersById.Clear();
            _principalMatchCache.Clear();
        }
    }

    private static PlatformCatalog Read(string path) =>
        JsonSerializer.Deserialize<PlatformCatalog>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException($"ACL-Datei '{path}' ist leer.");

    private static PermissionOverrideDocument ReadOverrides(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        try
        {
            return JsonSerializer.Deserialize<PermissionOverrideDocument>(
                File.ReadAllText(path),
                options)
                ?? new PermissionOverrideDocument();
        }
        catch (JsonException) when (File.Exists(path + ".bak"))
        {
            return JsonSerializer.Deserialize<PermissionOverrideDocument>(
                File.ReadAllText(path + ".bak"),
                options)
                ?? new PermissionOverrideDocument();
        }
    }

    private static void ApplyOverrides(
        PlatformCatalog target,
        PermissionOverrideDocument overrides)
    {
        foreach (var folderOverride in overrides.Folders)
        {
            var folder = target.Folders.FirstOrDefault(x =>
                x.Id.Equals(folderOverride.Id, StringComparison.OrdinalIgnoreCase));

            if (folder is not null)
                folder.Grants = CloneGrants(folderOverride.Grants);
        }

        foreach (var reportOverride in overrides.Reports)
        {
            var report = target.Reports.FirstOrDefault(x =>
                x.Id.Equals(reportOverride.Id, StringComparison.OrdinalIgnoreCase));

            if (report is not null)
                report.Grants = CloneGrants(reportOverride.Grants);
        }
    }

    private static List<ReportGrant> CloneGrants(
        IEnumerable<ReportGrant> grants) =>
        grants
            .Where(grant => !string.IsNullOrWhiteSpace(grant.Principal))
            .Select(grant => new ReportGrant
            {
                PrincipalType = string.IsNullOrWhiteSpace(grant.PrincipalType)
                    ? "Group"
                    : grant.PrincipalType,
                Principal = grant.Principal.Trim(),
                Permissions = grant.Permissions
                    .Where(permission => !string.IsNullOrWhiteSpace(permission))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .ToList();

    private static void Merge(PlatformCatalog target, PlatformCatalog imported)
    {
        foreach (var folder in imported.Folders)
        {
            var existing = target.Folders.FirstOrDefault(x =>
                x.Id.Equals(folder.Id, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                target.Folders.Add(folder);
            }
            else
            {
                existing.Grants.AddRange(folder.Grants);
            }
        }

        foreach (var report in imported.Reports)
        {
            var sameId = target.Reports.FirstOrDefault(x =>
                x.Id.Equals(report.Id, StringComparison.OrdinalIgnoreCase));

            if (sameId is not null)
            {
                sameId.Grants.AddRange(report.Grants);
                continue;
            }

            // The imported SSRS Kundencockpit deliberately points at the custom
            // modern route. Prefer the imported catalog item over the bootstrap
            // pilot entry so it appears only once in the portal.
            target.Reports.RemoveAll(x =>
                !string.IsNullOrWhiteSpace(report.Url)
                && x.Url.Equals(report.Url, StringComparison.OrdinalIgnoreCase));

            target.Reports.Add(report);
        }
    }

    private static void MergePackages(
        PlatformCatalog target,
        IReadOnlyList<DynReportSystem.Models.LoadedDynReportPackage> packages)
    {
        foreach (var package in packages)
        {
            var manifest = package.Manifest;
            var folderId = EnsureFolderPath(
                target,
                string.IsNullOrWhiteSpace(manifest.FolderPath)
                    ? ParentPath(manifest.Path)
                    : manifest.FolderPath);

            var grants = manifest.Grants
                .Select(grant => new ReportGrant
                {
                    PrincipalType = grant.PrincipalType,
                    Principal = grant.Principal,
                    Permissions = grant.Permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                })
                .ToList();

            var report = target.Reports.FirstOrDefault(x =>
                x.Id.Equals(manifest.ReportId, StringComparison.OrdinalIgnoreCase));

            if (report is null)
            {
                report = new ReportItem { Id = manifest.ReportId };
                target.Reports.Add(report);
            }

            report.FolderId = folderId;
            report.Title = manifest.Title;
            report.Url = $"/reports/package/{Uri.EscapeDataString(manifest.ReportId)}";
            report.Datasets = package.Document.Datasets.Select(x => x.Id).ToList();

            // A self-contained package owns its runtime ACL. This lets a converted
            // report survive removal of the ReportServer migration database/RDL.
            if (grants.Count > 0)
                report.Grants = grants;
        }
    }

    private static string EnsureFolderPath(PlatformCatalog target, string path)
    {
        var segments = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        string? parentId = null;
        var accumulated = "";

        foreach (var segment in segments)
        {
            accumulated += "/" + segment;

            var existing = target.Folders.FirstOrDefault(folder =>
                string.Equals(folder.ParentId, parentId, StringComparison.OrdinalIgnoreCase)
                && folder.Title.Equals(segment, StringComparison.CurrentCultureIgnoreCase));

            if (existing is null)
            {
                existing = new ReportFolder
                {
                    Id = StableFolderId(accumulated),
                    ParentId = parentId,
                    Title = segment,
                    Grants = []
                };
                target.Folders.Add(existing);
            }

            parentId = existing.Id;
        }

        return parentId ?? StableFolderId("/");
    }

    private static string ParentPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        return index <= 0 ? "/" : normalized[..index];
    }

    private static string StableFolderId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return "dyn-folder-" + Convert.ToHexString(bytes[..10]).ToLowerInvariant();
    }

    private static void Validate(PlatformCatalog config)
    {
        if (config.Folders
                .GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Count() > 1)
            || config.Reports
                .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Count() > 1))
            throw new InvalidDataException("Doppelte Ordner-/Berichts-ID in der ACL.");
    }

    private bool Allowed(
        IEnumerable<ReportGrant> grants,
        ClaimsPrincipal user,
        string permission) =>
        grants.Any(g =>
            g.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase)
            && PrincipalMatches(g, user));

    private bool PrincipalMatches(ReportGrant grant, ClaimsPrincipal user)
    {
        if (string.IsNullOrWhiteSpace(grant.Principal))
            return false;

        if (grant.Principal.Equals(@"Jeder", StringComparison.OrdinalIgnoreCase)
            || grant.Principal.Equals("Jeder", StringComparison.OrdinalIgnoreCase)
            || grant.Principal.Equals("Everyone", StringComparison.OrdinalIgnoreCase))
            return user.Identity?.IsAuthenticated == true;

        var userName = user.Identity?.Name ?? "";
        if (grant.PrincipalType.Equals("User", StringComparison.OrdinalIgnoreCase))
            return string.Equals(userName, grant.Principal, StringComparison.OrdinalIgnoreCase);

        if (!grant.PrincipalType.Equals("WindowsPrincipal", StringComparison.OrdinalIgnoreCase)
            && !grant.PrincipalType.Equals("Group", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(userName, grant.Principal, StringComparison.OrdinalIgnoreCase))
            return true;

        // Imported SSRS permissions contain many repeated Windows principals.
        // Calling WindowsPrincipal.IsInRole for every report/folder grant can
        // otherwise result in tens of thousands of domain/group checks during
        // the first portal render. Cache each user/principal pair once.
        var key = $"{userName}\n{grant.Principal}";
        return _principalMatchCache.GetOrAdd(key, _ =>
        {
            try
            {
                return user.IsInRole(grant.Principal);
            }
            catch
            {
                return false;
            }
        });
    }
}
