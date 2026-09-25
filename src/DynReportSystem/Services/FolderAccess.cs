using System.Security.Claims;
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

/// <summary>
/// Explicit allow only. Rights are inherited from ancestor folders.
/// Windows users and AD groups are evaluated server-side.
/// </summary>
public sealed class FolderAccess(IWebHostEnvironment host)
{
    private readonly string _path = Path.Combine(host.ContentRootPath, "config", "permissions.json");
    private readonly object _lock = new();
    private PlatformCatalog? _catalog;
    private DateTime _mtime;

    public PlatformCatalog Catalog
    {
        get
        {
            var time = File.GetLastWriteTimeUtc(_path);
            if (time == DateTime.MinValue)
                throw new FileNotFoundException("ACL-Konfiguration fehlt.", _path);

            lock (_lock)
            {
                if (_catalog is not null && time == _mtime) return _catalog;

                var config = JsonSerializer.Deserialize<PlatformCatalog>(
                    File.ReadAllText(_path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("ACL-Datei ist leer.");

                if (config.Folders.GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)
                    || config.Reports.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                    throw new InvalidDataException("Doppelte Ordner-/Berichts-ID in der ACL.");

                _catalog = config;
                _mtime = time;
                return config;
            }
        }
    }

    public bool Can(ClaimsPrincipal user, string reportId, string permission)
    {
        if (user.Identity?.IsAuthenticated != true) return false;

        var catalog = Catalog;
        var report = catalog.Reports.FirstOrDefault(r => r.Id.Equals(reportId, StringComparison.OrdinalIgnoreCase));
        if (report is null) return false;

        if (Allowed(report.Grants, user, permission)) return true;

        var folderId = report.FolderId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrWhiteSpace(folderId) && visited.Add(folderId))
        {
            var folder = catalog.Folders.FirstOrDefault(f => f.Id.Equals(folderId, StringComparison.OrdinalIgnoreCase));
            if (folder is null) return false;
            if (Allowed(folder.Grants, user, permission)) return true;
            folderId = folder.ParentId;
        }

        return false;
    }

    public bool CanRunDataset(ClaimsPrincipal user, string dataset) =>
        Catalog.Reports.Any(r =>
            r.Datasets.Contains(dataset, StringComparer.OrdinalIgnoreCase)
            && Can(user, r.Id, "View")
            && Can(user, r.Id, "Run"));

    private static bool Allowed(IEnumerable<ReportGrant> grants, ClaimsPrincipal user, string permission) =>
        grants.Any(g =>
            g.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(g.Principal)
            && (g.PrincipalType.Equals("User", StringComparison.OrdinalIgnoreCase)
                ? string.Equals(user.Identity?.Name, g.Principal, StringComparison.OrdinalIgnoreCase)
                : g.PrincipalType.Equals("Group", StringComparison.OrdinalIgnoreCase) && user.IsInRole(g.Principal)));
}
