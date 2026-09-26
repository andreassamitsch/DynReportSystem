using System.Text.Json;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

public sealed class ImportedPortalCatalog(IWebHostEnvironment host)
{
    private readonly string _path = Path.Combine(host.ContentRootPath, "migration", "catalog.json");
    private readonly object _gate = new();
    private DateTime _mtime;
    private ImportedPortalCatalogModel? _catalog;

    public bool Exists => File.Exists(_path);

    public ImportedPortalCatalogModel Catalog
    {
        get
        {
            if (!File.Exists(_path))
                return new ImportedPortalCatalogModel();

            var mtime = File.GetLastWriteTimeUtc(_path);

            lock (_gate)
            {
                if (_catalog is not null && mtime == _mtime)
                    return _catalog;

                _catalog = JsonSerializer.Deserialize<ImportedPortalCatalogModel>(
                    File.ReadAllText(_path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new ImportedPortalCatalogModel();

                _mtime = mtime;
                return _catalog;
            }
        }
    }

    public ImportedReport GetReport(string id) =>
        Catalog.Reports.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Importierter Bericht '{id}' wurde nicht gefunden.");

    public string ResolveContentPath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(host.ContentRootPath, relativePath));
        var root = Path.GetFullPath(host.ContentRootPath);

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ungültiger Migrationspfad.");

        return full;
    }

    public ImportedSharedDataset? FindSharedDataset(string reference)
    {
        var normalized = NormalizePath(reference);
        return Catalog.SharedDatasets.FirstOrDefault(x =>
            NormalizePath(x.Path).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public ImportedDataSource? FindDataSource(string reference)
    {
        var normalized = NormalizePath(reference);
        return Catalog.DataSources.FirstOrDefault(x =>
            NormalizePath(x.Path).Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || x.Title.Equals(reference.Trim('/'), StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var value = path.Trim().Replace('\\', '/');
        if (!value.StartsWith('/'))
            value = "/" + value;
        return value.TrimEnd('/');
    }
}
