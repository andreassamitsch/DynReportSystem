using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

public sealed class RdlCatalog(IConfiguration config)
{
    private static readonly Regex SimpleParameter = new(
        @"^=Parameters!([A-Za-z_][A-Za-z_0-9]*)\.Value$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AllowedParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Start", "Ende", "Kunde", "Vertriebsmitarbeiter", "Embed", "OPAnsicht", "ReklAnsicht"
    };

    private readonly object _lock = new();
    private DateTime _loadedUtc;
    private IReadOnlyDictionary<string, RdlDataset>? _catalog;

    public string FullPath
    {
        get
        {
            var path = config["Cockpit:RdlPath"] ?? "Reports/Kundencockpit.rdl";
            return Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, path));
        }
    }

    public IReadOnlyDictionary<string, RdlDataset> GetAll()
    {
        var path = FullPath;
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "Die Kundencockpit-RDL fehlt. Datei nach 'Reports/Kundencockpit.rdl' kopieren.",
                path);

        var updated = File.GetLastWriteTimeUtc(path);

        lock (_lock)
        {
            if (_catalog is not null && updated == _loadedUtc)
                return _catalog;

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };

            using var xml = XmlReader.Create(path, settings);
            var report = XDocument.Load(xml);
            XNamespace ns = report.Root?.Name.Namespace
                ?? throw new InvalidDataException("RDL ist leer.");

            var map = new Dictionary<string, RdlDataset>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in report.Descendants(ns + "DataSet"))
            {
                var name = item.Attribute("Name")?.Value ?? "";
                var query = item.Element(ns + "Query");
                var sql = query?.Element(ns + "CommandText")?.Value ?? "";

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sql))
                    continue;

                var bindings = new List<QueryBinding>();

                foreach (var p in query!.Element(ns + "QueryParameters")?.Elements(ns + "QueryParameter") ?? [])
                {
                    var sqlName = p.Attribute("Name")?.Value ?? "";
                    var expression = p.Element(ns + "Value")?.Value.Trim() ?? "";
                    var match = SimpleParameter.Match(expression);

                    if (sqlName.Length < 2
                        || sqlName[0] != '@'
                        || !match.Success
                        || !AllowedParameters.Contains(match.Groups[1].Value))
                        throw new InvalidDataException(
                            $"Dataset '{name}': nicht unterstützter RDL-Parameter '{sqlName}'.");

                    bindings.Add(new QueryBinding(sqlName, match.Groups[1].Value));
                }

                var fields = item.Element(ns + "Fields")?.Elements(ns + "Field")
                    .Select(f => f.Element(ns + "DataField")?.Value ?? f.Attribute("Name")?.Value ?? "")
                    .Where(f => f.Length > 0)
                    .ToArray() ?? [];

                map.Add(name, new RdlDataset(name, sql, bindings, fields));
            }

            if (map.Count == 0)
                throw new InvalidDataException("Keine ausführbaren Datasets in der RDL gefunden.");

            _catalog = map;
            _loadedUtc = updated;
            return map;
        }
    }
}
