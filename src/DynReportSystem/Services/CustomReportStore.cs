using System.Text.Json;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

public sealed class CustomReportStore(
    ImportedPortalCatalog portal,
    ILogger<CustomReportStore> logger)
{
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DynReportSystem",
        "CustomReports");

    private Dictionary<string, CachedCustomReport>? _cache;
    private DateTime _cacheStamp;

    public string DirectoryPath => _directory;

    public CustomReportDefinition? GetForReport(string reportId)
    {
        EnsureLoaded();
        lock (_gate)
        {
            return _cache!.TryGetValue(reportId, out var item)
                ? item.Definition
                : null;
        }
    }

    public IReadOnlyList<InstalledCustomReport> List()
    {
        EnsureLoaded();
        lock (_gate)
        {
            return _cache!.Values
                .Select(x => new InstalledCustomReport(
                    x.Definition.TargetReportId,
                    x.Definition.TargetPath,
                    x.Definition.Title,
                    x.Definition.Renderer,
                    x.Path,
                    x.ModifiedUtc))
                .OrderBy(x => x.TargetPath, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
    }

    public CustomReportDefinition ParseAndValidate(ReadOnlySpan<byte> content)
    {
        var definition = JsonSerializer.Deserialize<CustomReportDefinition>(content, _json)
            ?? throw new InvalidDataException("Die DynReport-Datei ist leer.");

        Validate(definition);
        return definition;
    }

    public async Task<CustomReportDefinition> ImportAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);

        if (memory.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Die DynReport-Datei ist größer als 2 MB.");

        var definition = ParseAndValidate(memory.ToArray());
        Directory.CreateDirectory(_directory);

        var path = Path.Combine(
            _directory,
            SafeFileName(definition.TargetReportId) + ".dynreport");

        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, memory.ToArray(), cancellationToken);
        File.Move(temp, path, true);

        Invalidate();
        logger.LogInformation(
            "Custom report definition imported for {ReportId} ({Path})",
            definition.TargetReportId,
            definition.TargetPath);

        return definition;
    }

    public void Remove(string reportId)
    {
        EnsureLoaded();

        string? path;
        lock (_gate)
        {
            path = _cache!.TryGetValue(reportId, out var item) ? item.Path : null;
        }

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            File.Delete(path);

        Invalidate();
    }

    public void Validate(CustomReportDefinition definition)
    {
        if (!definition.SchemaVersion.Equals("1.0", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Nicht unterstützte DynReport-Dateiversion '{definition.SchemaVersion}'.");

        if (string.IsNullOrWhiteSpace(definition.TargetReportId))
            throw new InvalidDataException("TargetReportId fehlt.");

        var report = portal.Catalog.Reports.FirstOrDefault(x =>
            x.Id.Equals(definition.TargetReportId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Der Zielbericht '{definition.TargetReportId}' existiert im importierten SSRS-Katalog nicht.");

        if (!string.IsNullOrWhiteSpace(definition.TargetPath)
            && !ImportedPortalCatalog.NormalizePath(definition.TargetPath).Equals(
                ImportedPortalCatalog.NormalizePath(report.Path),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"TargetPath '{definition.TargetPath}' gehört nicht zum Bericht '{report.Path}'.");
        }

        if (string.IsNullOrWhiteSpace(definition.Title))
            definition.Title = report.Title;

        definition.TargetPath = report.Path;

        if (string.IsNullOrWhiteSpace(definition.PrimaryDataset))
            throw new InvalidDataException("PrimaryDataset fehlt.");

        if (!definition.Renderer.Equals("production-tv-overview", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Renderer '{definition.Renderer}' wird von dieser DynReport-Version nicht unterstützt.");

        if (definition.ProductionTv is null)
            throw new InvalidDataException("ProductionTv-Konfiguration fehlt.");

        if (definition.AutoRefreshSeconds is > 0 and < 30)
            throw new InvalidDataException("AutoRefreshSeconds muss 0 oder mindestens 30 Sekunden betragen.");
    }

    private void EnsureLoaded()
    {
        Directory.CreateDirectory(_directory);

        var stamp = Directory
            .EnumerateFiles(_directory, "*.dynreport", SearchOption.TopDirectoryOnly)
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        lock (_gate)
        {
            if (_cache is not null && stamp == _cacheStamp)
                return;

            var loaded = new Dictionary<string, CachedCustomReport>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in Directory.EnumerateFiles(_directory, "*.dynreport"))
            {
                try
                {
                    var definition = JsonSerializer.Deserialize<CustomReportDefinition>(
                        File.ReadAllText(path),
                        _json);

                    if (definition is null)
                        continue;

                    Validate(definition);
                    loaded[definition.TargetReportId] = new CachedCustomReport(
                        definition,
                        path,
                        File.GetLastWriteTimeUtc(path));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not load custom report file {Path}", path);
                }
            }

            _cache = loaded;
            _cacheStamp = stamp;
        }
    }

    private void Invalidate()
    {
        lock (_gate)
        {
            _cache = null;
            _cacheStamp = DateTime.MinValue;
        }
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }

    private sealed record CachedCustomReport(
        CustomReportDefinition Definition,
        string Path,
        DateTime ModifiedUtc);
}
