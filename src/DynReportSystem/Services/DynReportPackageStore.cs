using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DynReportSystem.Models;
using Microsoft.Data.SqlClient;

namespace DynReportSystem.Services;

public sealed class DynReportPackageStore(
    DynReportMetadataStore metadata,
    DynReportSqlPolicyValidator queryPolicy,
    IConfiguration config,
    ILogger<DynReportPackageStore> logger)
{
    public const string ManifestEntry = "manifest.json";
    public const string ReportEntry = "report.json";

    private const long MaxPackageBytes = 25L * 1024 * 1024;
    private const long MaxEntryBytes = 10L * 1024 * 1024;
    private const long MaxExpandedBytes = 50L * 1024 * 1024;
    private const int MaxEntries = 200;
    private const double MaxCompressionRatio = 100d;

    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DynReportSystem",
        "Packages");

    private readonly string _revisionsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DynReportSystem",
        "Revisions");

    private Dictionary<string, LoadedDynReportPackage>? _cache;
    private DateTime _directoryStamp;
    private long _generation;

    public string RootPath => _root;
    public string RevisionsPath => _revisionsRoot;

    /// <summary>
    /// Cheap in-process change token used by the portal/ACL catalog. Package
    /// imports/designer saves increment it so callers do not have to rescan the
    /// package directory for every permission or render check.
    /// </summary>
    public long Generation => Interlocked.Read(ref _generation);

    public LoadedDynReportPackage? Get(string reportId)
    {
        EnsureLoaded();
        lock (_gate)
        {
            return _cache!.TryGetValue(reportId, out var package)
                ? package
                : null;
        }
    }

    public IReadOnlyList<LoadedDynReportPackage> List()
    {
        EnsureLoaded();
        lock (_gate)
        {
            return _cache!.Values
                .OrderBy(x => x.Manifest.Path, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
    }

    public async Task<LoadedDynReportPackage> ImportAsync(
        Stream stream,
        string? importedBy = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_revisionsRoot);

        var temp = Path.Combine(_root, $".import-{Guid.NewGuid():N}.dynreport");

        try
        {
            await using (var output = File.Create(temp))
            {
                await stream.CopyToAsync(output, cancellationToken);
            }

            var info = new FileInfo(temp);
            if (info.Length <= 0 || info.Length > MaxPackageBytes)
                throw new InvalidDataException("Die DynReport-Datei muss zwischen 1 Byte und 25 MB groß sein.");

            var loaded = LoadFile(temp);
            var target = Path.Combine(_root, SafeFileName(loaded.Manifest.ReportId) + ".dynreport");

            if (File.Exists(target))
                CreateRevision(target, loaded.Manifest.ReportId);

            File.Move(temp, target, true);
            Invalidate();

            var installed = LoadFile(target);
            await MirrorToMetadataAsync(installed, importedBy, cancellationToken);

            logger.LogInformation(
                "DynReport package {ReportId} version {Version} imported by {User}",
                loaded.Manifest.ReportId,
                loaded.Manifest.Version,
                importedBy ?? "unknown");

            return installed;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public async Task<LoadedDynReportPackage> SaveDesignerPackageAsync(
        LoadedDynReportPackage package,
        string editedBy,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_root, SafeFileName(package.Manifest.ReportId) + ".dynreport");
        Directory.CreateDirectory(_root);

        package.Manifest.ModifiedUtc = DateTime.UtcNow;

        var temp = path + ".tmp";
        if (File.Exists(temp))
            File.Delete(temp);

        await using (var file = File.Create(temp))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
        {
            await WriteJsonEntryAsync(archive, ManifestEntry, package.Manifest, cancellationToken);
            await WriteJsonEntryAsync(archive, ReportEntry, package.Document, cancellationToken);

            foreach (var item in package.TextFiles)
            {
                if (item.Key.Equals(ManifestEntry, StringComparison.OrdinalIgnoreCase)
                    || item.Key.Equals(ReportEntry, StringComparison.OrdinalIgnoreCase))
                    continue;

                await WriteTextEntryAsync(archive, item.Key, item.Value, cancellationToken);
            }
        }

        // Validate the complete package before touching the currently published
        // report. A broken designer save must never replace the last good version.
        _ = LoadFile(temp);

        if (File.Exists(path))
            CreateRevision(path, package.Manifest.ReportId);

        File.Move(temp, path, true);
        Invalidate();

        var installed = LoadFile(path);
        await MirrorToMetadataAsync(installed, editedBy, cancellationToken);

        logger.LogInformation(
            "DynReport package {ReportId} saved by designer user {User}",
            package.Manifest.ReportId,
            editedBy);

        return installed;
    }

    public IReadOnlyList<DynReportRevision> Revisions(string reportId)
    {
        var dir = Path.Combine(_revisionsRoot, SafeFileName(reportId));
        if (!Directory.Exists(dir))
            return [];

        var revisions = new List<DynReportRevision>();

        foreach (var file in Directory.EnumerateFiles(dir, "*.dynreport"))
        {
            try
            {
                var package = LoadFile(file);
                revisions.Add(new DynReportRevision(
                    reportId,
                    package.Manifest.Version,
                    file,
                    File.GetCreationTimeUtc(file)));
            }
            catch { }
        }

        return revisions
            .OrderByDescending(x => x.CreatedUtc)
            .ToArray();
    }

    public void Remove(string reportId)
    {
        var path = Path.Combine(_root, SafeFileName(reportId) + ".dynreport");
        if (File.Exists(path))
        {
            CreateRevision(path, reportId);
            File.Delete(path);
        }

        Invalidate();
    }

    public LoadedDynReportPackage Validate(Stream stream)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"dynreport-{Guid.NewGuid():N}.dynreport");
        try
        {
            using (var output = File.Create(temp))
                stream.CopyTo(output);

            return LoadFile(temp);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public string ComputeSha256(string reportId)
    {
        var package = Get(reportId)
            ?? throw new FileNotFoundException($"DynReport '{reportId}' wurde nicht gefunden.");

        using var stream = File.OpenRead(package.FilePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private void EnsureLoaded()
    {
        Directory.CreateDirectory(_root);

        // Directory timestamp is a single cheap filesystem metadata lookup.
        // The old implementation enumerated every *.dynreport file on every
        // Get/List call, which became very expensive because the portal calls
        // package lookups repeatedly while rendering report cards/ACLs.
        var directoryStamp = Directory.GetLastWriteTimeUtc(_root);

        lock (_gate)
        {
            if (_cache is not null && directoryStamp == _directoryStamp)
                return;

            var loaded = new Dictionary<string, LoadedDynReportPackage>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in Directory.EnumerateFiles(_root, "*.dynreport"))
            {
                try
                {
                    var package = LoadFile(path);
                    loaded[package.Manifest.ReportId] = package;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not load DynReport package {Path}", path);
                }
            }

            _cache = loaded;
            _directoryStamp = directoryStamp;
            Interlocked.Increment(ref _generation);
        }
    }

    private LoadedDynReportPackage LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxEntries)
            throw new InvalidDataException("Ungültige DynReport-Paketstruktur.");

        long expandedBytes = 0;

        foreach (var entry in archive.Entries)
        {
            ValidateEntryName(entry.FullName);

            if (entry.Length < 0 || entry.Length > MaxEntryBytes)
                throw new InvalidDataException(
                    $"Paketdatei '{entry.FullName}' überschreitet das Größenlimit.");

            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaxExpandedBytes)
                throw new InvalidDataException(
                    "Das entpackte DynReport-Paket überschreitet 50 MB.");

            if (entry.Length > 0)
            {
                if (entry.CompressedLength <= 0)
                    throw new InvalidDataException(
                        $"Paketdatei '{entry.FullName}' hat eine ungültige Kompressionsgröße.");

                var ratio = entry.Length / (double)entry.CompressedLength;
                if (ratio > MaxCompressionRatio)
                    throw new InvalidDataException(
                        $"Paketdatei '{entry.FullName}' hat eine verdächtige Kompressionsrate.");
            }
        }

        var manifestEntry = FindEntry(archive, ManifestEntry)
            ?? throw new InvalidDataException("manifest.json fehlt.");

        var reportEntry = FindEntry(archive, ReportEntry)
            ?? throw new InvalidDataException("report.json fehlt.");

        var manifest = ReadJsonEntry<DynReportManifest>(manifestEntry)
            ?? throw new InvalidDataException("manifest.json ist leer.");

        var document = ReadJsonEntry<DynReportDocument>(reportEntry)
            ?? throw new InvalidDataException("report.json ist leer.");

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                continue;

            if (entry.FullName.Equals(ManifestEntry, StringComparison.OrdinalIgnoreCase)
                || entry.FullName.Equals(ReportEntry, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!entry.FullName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                && !entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !entry.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                && !entry.FullName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var reader = new StreamReader(entry.Open());
            files[entry.FullName] = reader.ReadToEnd();
        }

        ValidatePackage(manifest, document, files);

        return new LoadedDynReportPackage
        {
            Manifest = manifest,
            Document = document,
            FilePath = Path.GetFullPath(path),
            ModifiedUtc = File.GetLastWriteTimeUtc(path),
            TextFiles = files
        };
    }

    private void ValidatePackage(
        DynReportManifest manifest,
        DynReportDocument document,
        IReadOnlyDictionary<string, string> files)
    {
        if (!manifest.SchemaVersion.Equals("2.0", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Nicht unterstützte DynReport-Schemaversion '{manifest.SchemaVersion}'. Erwartet: 2.0.");

        if (string.IsNullOrWhiteSpace(manifest.ReportId))
            throw new InvalidDataException("ReportId fehlt.");

        if (string.IsNullOrWhiteSpace(manifest.Title))
            throw new InvalidDataException("Title fehlt.");

        if (string.IsNullOrWhiteSpace(manifest.Path))
            throw new InvalidDataException("Path fehlt.");

        if (document.Pages.Count == 0)
            throw new InvalidDataException("Der Bericht enthält keine Seite.");

        var sourceIds = document.DataSources
            .Select(x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (sourceIds.Count != document.DataSources.Count)
            throw new InvalidDataException("Doppelte DataSource-ID.");

        var dataSetIds = document.Datasets
            .Select(x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (dataSetIds.Count != document.Datasets.Count)
            throw new InvalidDataException("Doppelte Dataset-ID.");

        foreach (var source in document.DataSources)
        {
            if (string.IsNullOrWhiteSpace(source.DefaultConnectionString))
                continue;

            try
            {
                var builder = new SqlConnectionStringBuilder(source.DefaultConnectionString);

                if (!string.IsNullOrWhiteSpace(builder.UserID)
                    || !string.IsNullOrWhiteSpace(builder.Password))
                {
                    throw new InvalidDataException(
                        $"Datenquelle '{source.Id}' enthält Zugangsdaten im Report-Paket. " +
                        "Native DynReport-Pakete dürfen keine SQL-Credentials enthalten.");
                }
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException(
                    $"Datenquelle '{source.Id}' enthält ungültige Connection-Metadaten.",
                    ex);
            }
        }

        foreach (var dataSet in document.Datasets)
        {
            if (!sourceIds.Contains(dataSet.DataSourceId))
                throw new InvalidDataException(
                    $"Dataset '{dataSet.Id}' referenziert unbekannte Datenquelle '{dataSet.DataSourceId}'.");

            if (string.IsNullOrWhiteSpace(dataSet.QueryFile)
                || !files.TryGetValue(dataSet.QueryFile, out var queryText))
                throw new InvalidDataException(
                    $"SQL-Datei '{dataSet.QueryFile}' für Dataset '{dataSet.Id}' fehlt.");

            queryPolicy.Validate(dataSet, queryText);
        }

        var parameterNames = document.Parameters
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dataSet in document.Datasets)
        {
            foreach (var binding in dataSet.Parameters)
            {
                if (!parameterNames.Contains(binding.ParameterName))
                    throw new InvalidDataException(
                        $"Dataset '{dataSet.Id}' referenziert unbekannten Parameter '{binding.ParameterName}'.");
            }
        }

        if (document.Settings.AutoRefreshSeconds is > 0 and < 30)
            throw new InvalidDataException("AutoRefreshSeconds muss 0 oder mindestens 30 Sekunden sein.");

        foreach (var page in document.Pages)
        {
            if (page.Columns is < 1 or > 24)
                throw new InvalidDataException($"Ungültige Spaltenzahl auf Seite '{page.Id}'.");

            foreach (var component in page.Components)
                ValidateVisual(component, dataSetIds);
        }
    }

    private static void ValidateVisual(DynVisual visual, HashSet<string> dataSetIds)
    {
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "heading",
            "metric-strip",
            "grouped-board",
            "table",
            "chart",
            "text"
        };

        if (!supported.Contains(visual.Type))
            throw new InvalidDataException(
                $"Visual-Typ '{visual.Type}' wird von der Runtime nicht unterstützt.");

        if (!string.IsNullOrWhiteSpace(visual.Dataset)
            && !dataSetIds.Contains(visual.Dataset))
            throw new InvalidDataException(
                $"Visual '{visual.Id}' referenziert unbekanntes Dataset '{visual.Dataset}'.");

        if (visual.Type.Equals("grouped-board", StringComparison.OrdinalIgnoreCase)
            && visual.Board is null)
            throw new InvalidDataException($"Grouped board '{visual.Id}' enthält keine Board-Definition.");
    }

    private void CreateRevision(string currentPath, string reportId)
    {
        if (!File.Exists(currentPath))
            return;

        Directory.CreateDirectory(_revisionsRoot);
        var reportDir = Path.Combine(_revisionsRoot, SafeFileName(reportId));
        Directory.CreateDirectory(reportDir);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var revision = Path.Combine(reportDir, $"{stamp}.dynreport");
        File.Copy(currentPath, revision, true);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name) =>
        archive.Entries.FirstOrDefault(x =>
            x.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private T? ReadJsonEntry<T>(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return JsonSerializer.Deserialize<T>(reader.ReadToEnd(), _json);
    }

    private async Task WriteJsonEntryAsync<T>(
        ZipArchive archive,
        string name,
        T value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, _json, cancellationToken);
    }

    private static async Task WriteTextEntryAsync(
        ZipArchive archive,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        ValidateEntryName(name);
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(value.AsMemory(), cancellationToken);
    }

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.StartsWith("/", StringComparison.Ordinal)
            || name.StartsWith("\\", StringComparison.Ordinal)
            || name.Contains("..", StringComparison.Ordinal)
            || name.Contains(':'))
            throw new InvalidDataException($"Ungültiger Paketpfad '{name}'.");
    }

    private async Task MirrorToMetadataAsync(
        LoadedDynReportPackage package,
        string? userName,
        CancellationToken cancellationToken)
    {
        if (!metadata.IsConfigured)
            return;

        try
        {
            var bytes = await File.ReadAllBytesAsync(package.FilePath, cancellationToken);
            await metadata.PublishPackageAsync(
                package,
                bytes,
                userName,
                cancellationToken);

            await metadata.WriteAuditAsync(
                new DynAuditEvent(
                    Guid.NewGuid(),
                    "report.publish",
                    "success",
                    userName,
                    package.Manifest.ReportId,
                    DetailsJson: DynAuditEvent.Details(new
                    {
                        package.Manifest.Version,
                        package.Manifest.SchemaVersion,
                        PackageBytes = bytes.LongLength
                    })),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // During the transition the filesystem remains the runtime source.
            // Metadata failures are surfaced in logs/readiness but do not corrupt
            // the already validated local package.
            logger.LogError(
                ex,
                "Could not mirror DynReport package {ReportId} to metadata database",
                package.Manifest.ReportId);

            if (config.GetValue("Metadata:FailPublishWhenUnavailable", false))
                throw;
        }
    }

    private void Invalidate()
    {
        lock (_gate)
        {
            _cache = null;
            _directoryStamp = DateTime.MinValue;
            Interlocked.Increment(ref _generation);
        }
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
