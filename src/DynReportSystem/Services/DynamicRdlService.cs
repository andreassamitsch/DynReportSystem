using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

public sealed partial class DynamicRdlService(ImportedPortalCatalog portal)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CachedDefinition> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public DynamicReportDefinition GetDefinition(string reportId)
    {
        var report = portal.GetReport(reportId);
        var sourceReport = report;

        if (string.IsNullOrWhiteSpace(sourceReport.RdlPath)
            && !string.IsNullOrWhiteSpace(report.SourcePath))
        {
            sourceReport = portal.Catalog.Reports.FirstOrDefault(x =>
                ImportedPortalCatalog.NormalizePath(x.Path).Equals(
                    ImportedPortalCatalog.NormalizePath(report.SourcePath),
                    StringComparison.OrdinalIgnoreCase))
                ?? report;
        }

        if (string.IsNullOrWhiteSpace(sourceReport.RdlPath))
            throw new FileNotFoundException($"Für '{report.Path}' wurde keine RDL importiert.");

        var fullPath = portal.ResolveContentPath(sourceReport.RdlPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Importierte RDL fehlt.", fullPath);

        var mtime = File.GetLastWriteTimeUtc(fullPath);

        lock (_gate)
        {
            if (_cache.TryGetValue(reportId, out var existing) && existing.Modified == mtime)
                return existing.Definition;

            var definition = ParseReport(report, fullPath);
            _cache[reportId] = new CachedDefinition(mtime, definition);
            return definition;
        }
    }

    public Dictionary<string, DynamicParameterValue> CreateInitialValues(DynamicReportDefinition definition)
    {
        var values = new Dictionary<string, DynamicParameterValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in definition.Parameters)
        {
            var state = new DynamicParameterValue
            {
                Name = parameter.Name,
                DataType = parameter.DataType,
                MultiValue = parameter.MultiValue
            };

            foreach (var expression in parameter.DefaultExpressions)
            {
                var value = EvaluateDefault(expression, parameter.DataType);
                if (value is not null)
                    state.Values.Add(value);
            }

            values[parameter.Name] = state;
        }

        return values;
    }

    private DynamicReportDefinition ParseReport(ImportedReport report, string fullPath)
    {
        var doc = LoadXml(fullPath);
        var root = doc.Root ?? throw new InvalidDataException("RDL ist leer.");
        var ns = root.Name.Namespace;

        var dataSources = new Dictionary<string, DynamicDataSourceDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in root.Element(ns + "DataSources")?.Elements(ns + "DataSource") ?? [])
        {
            var name = element.Attribute("Name")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var reference = Text(element, ns + "DataSourceReference");
            var props = element.Element(ns + "ConnectionProperties");

            dataSources[name] = new DynamicDataSourceDefinition
            {
                Name = name,
                Reference = reference,
                Provider = Text(props, ns + "DataProvider") ?? "SQL",
                ConnectString = Text(props, ns + "ConnectString")
            };
        }

        var parameters = ParseParameters(root, ns);
        var dataSets = new Dictionary<string, DynamicDatasetDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in root.Element(ns + "DataSets")?.Elements(ns + "DataSet") ?? [])
        {
            var parsed = ParseReportDataSet(element, ns);
            if (parsed is not null)
                dataSets[parsed.Name] = parsed;
        }

        var bodyDataSets = root.Descendants(ns + "DataSetName")
            .Select(x => x.Value.Trim())
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A report with only one dataset sometimes omits DataSetName on simple items.
        if (bodyDataSets.Count == 0 && dataSets.Count == 1)
            bodyDataSets.Add(dataSets.Keys.Single());

        return new DynamicReportDefinition
        {
            Report = report,
            Parameters = parameters,
            DataSources = dataSources,
            DataSets = dataSets,
            BodyDataSets = bodyDataSets
        };
    }

    private DynamicDatasetDefinition? ParseReportDataSet(XElement element, XNamespace ns)
    {
        var name = element.Attribute("Name")?.Value ?? "";
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var query = element.Element(ns + "Query");
        if (query is not null)
            return ParseQueryDataSet(name, query, element, ns, isShared: false, sharedReference: null);

        var shared = element.Element(ns + "SharedDataSet");
        if (shared is null)
            return null;

        var reference = Text(shared, ns + "SharedDataSetReference");
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        var sharedItem = portal.FindSharedDataset(reference);
        if (sharedItem?.DefinitionPath is null)
            return new DynamicDatasetDefinition
            {
                Name = name,
                IsShared = true,
                SharedDataSetReference = reference
            };

        var sharedPath = portal.ResolveContentPath(sharedItem.DefinitionPath);
        var sharedDoc = LoadXml(sharedPath);
        var sharedRoot = sharedDoc.Root ?? throw new InvalidDataException($"Shared Dataset '{reference}' ist leer.");
        var sns = sharedRoot.Name.Namespace;
        var dataSet = sharedRoot.Element(sns + "DataSet")
            ?? throw new InvalidDataException($"Shared Dataset '{reference}' enthält kein DataSet.");
        var sharedQuery = dataSet.Element(sns + "Query")
            ?? throw new InvalidDataException($"Shared Dataset '{reference}' enthält keine Query.");

        var parsed = ParseQueryDataSet(name, sharedQuery, dataSet, sns, isShared: true, sharedReference: reference);

        // A report may map its report parameters into the shared dataset.
        var reportBindings = ParseBindings(shared.Element(ns + "QueryParameters"), ns);
        if (reportBindings.Count != 0)
        {
            parsed = new DynamicDatasetDefinition
            {
                Name = parsed.Name,
                DataSourceName = parsed.DataSourceName,
                DataSourceReference = parsed.DataSourceReference,
                CommandText = parsed.CommandText,
                CommandType = parsed.CommandType,
                IsShared = true,
                SharedDataSetReference = reference,
                Bindings = reportBindings,
                Fields = parsed.Fields
            };
        }

        return parsed;
    }

    private static DynamicDatasetDefinition ParseQueryDataSet(
        string name,
        XElement query,
        XElement dataSetElement,
        XNamespace ns,
        bool isShared,
        string? sharedReference)
    {
        var dataSourceName = Text(query, ns + "DataSourceName");
        var dataSourceReference = Text(query, ns + "DataSourceReference");
        var commandText = Text(query, ns + "CommandText") ?? "";
        var commandType = Text(query, ns + "CommandType") ?? "Text";
        var bindings = ParseBindings(query.Element(ns + "QueryParameters"), ns);

        var fields = dataSetElement.Element(ns + "Fields")?.Elements(ns + "Field")
            .Select(field =>
                Text(field, ns + "DataField")
                ?? field.Attribute("Name")?.Value
                ?? "")
            .Where(x => x.Length > 0)
            .ToArray() ?? [];

        return new DynamicDatasetDefinition
        {
            Name = name,
            DataSourceName = dataSourceName,
            DataSourceReference = dataSourceReference,
            CommandText = commandText,
            CommandType = commandType,
            IsShared = isShared,
            SharedDataSetReference = sharedReference,
            Bindings = bindings,
            Fields = fields
        };
    }

    private static List<DynamicQueryBinding> ParseBindings(XElement? queryParameters, XNamespace ns)
    {
        var list = new List<DynamicQueryBinding>();

        foreach (var parameter in queryParameters?.Elements(ns + "QueryParameter") ?? [])
        {
            var sqlName = parameter.Attribute("Name")?.Value ?? "";
            var expression = Text(parameter, ns + "Value")?.Trim() ?? "";
            var match = ParameterReferenceRegex().Match(expression);

            list.Add(new DynamicQueryBinding(
                sqlName,
                match.Success ? match.Groups["name"].Value : null,
                expression));
        }

        return list;
    }

    private static IReadOnlyList<DynamicReportParameter> ParseParameters(XElement root, XNamespace ns)
    {
        var list = new List<DynamicReportParameter>();

        foreach (var parameter in root.Element(ns + "ReportParameters")?.Elements(ns + "ReportParameter") ?? [])
        {
            var name = parameter.Attribute("Name")?.Value ?? "";
            if (name.Length == 0)
                continue;

            var defaults = new List<string>();
            DynamicParameterDataSetReference? defaultDataSet = null;
            var defaultValue = parameter.Element(ns + "DefaultValue");

            if (defaultValue is not null)
            {
                defaultDataSet = ParseDataSetReference(defaultValue.Element(ns + "DataSetReference"), ns);

                foreach (var value in defaultValue.Element(ns + "Values")?.Elements(ns + "Value") ?? [])
                    defaults.Add(value.Value.Trim());
            }

            var staticValues = new List<DynamicParameterOption>();
            DynamicParameterDataSetReference? validDataSet = null;
            var validValues = parameter.Element(ns + "ValidValues");

            if (validValues is not null)
            {
                validDataSet = ParseDataSetReference(validValues.Element(ns + "DataSetReference"), ns);

                foreach (var item in validValues.Element(ns + "ParameterValues")?.Elements(ns + "ParameterValue") ?? [])
                {
                    var value = Text(item, ns + "Value") ?? "";
                    var label = Text(item, ns + "Label") ?? value;
                    staticValues.Add(new DynamicParameterOption(value, label));
                }
            }

            list.Add(new DynamicReportParameter
            {
                Name = name,
                Prompt = Text(parameter, ns + "Prompt") ?? name,
                DataType = Text(parameter, ns + "DataType") ?? "String",
                Hidden = Bool(Text(parameter, ns + "Hidden")),
                MultiValue = Bool(Text(parameter, ns + "MultiValue")),
                Nullable = Bool(Text(parameter, ns + "Nullable")),
                AllowBlank = Bool(Text(parameter, ns + "AllowBlank")),
                DefaultExpressions = defaults,
                DefaultDataSet = defaultDataSet,
                StaticValidValues = staticValues,
                ValidValuesDataSet = validDataSet
            });
        }

        return list;
    }

    private static DynamicParameterDataSetReference? ParseDataSetReference(XElement? element, XNamespace ns)
    {
        if (element is null)
            return null;

        var dataSet = Text(element, ns + "DataSetName");
        var value = Text(element, ns + "ValueField");

        if (string.IsNullOrWhiteSpace(dataSet) || string.IsNullOrWhiteSpace(value))
            return null;

        return new DynamicParameterDataSetReference(
            dataSet,
            value,
            Text(element, ns + "LabelField"));
    }

    private static XDocument LoadXml(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };

        using var reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader);
    }

    private static string? Text(XElement? parent, XName name) =>
        parent?.Element(name)?.Value;

    private static bool Bool(string? value) =>
        value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    private static string? EvaluateDefault(string expression, string dataType)
    {
        var value = expression.Trim();

        if (!value.StartsWith('='))
            return NormalizeLiteral(value, dataType);

        var normalized = value.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

        if (normalized is "=today()" or "=datevalue(today())")
            return DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (normalized == "=now()")
            return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        if (normalized == "=year(today())")
            return DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);

        if (normalized == "=month(today())")
            return DateTime.Today.Month.ToString(CultureInfo.InvariantCulture);

        var add = DateAddRegex().Match(value);
        if (add.Success)
        {
            var unit = add.Groups["unit"].Value.ToLowerInvariant();
            var amount = int.Parse(add.Groups["amount"].Value, CultureInfo.InvariantCulture);
            var basis = add.Groups["basis"].Value.Equals("Now", StringComparison.OrdinalIgnoreCase)
                ? DateTime.Now
                : DateTime.Today;

            var result = unit switch
            {
                "d" or "day" => basis.AddDays(amount),
                "m" or "month" => basis.AddMonths(amount),
                "yyyy" or "year" => basis.AddYears(amount),
                _ => basis
            };

            return result.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (DateSerialFirstDayRegex().IsMatch(value))
        {
            var now = DateTime.Today;
            return new DateTime(now.Year, now.Month, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static string NormalizeLiteral(string value, string dataType)
    {
        if (dataType.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";

        return value;
    }

    [GeneratedRegex(@"^=Parameters!(?<name>.+?)\.Value$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParameterReferenceRegex();

    [GeneratedRegex(
        @"^=DateAdd\(\s*[""'](?<unit>[^""']+)[""']\s*,\s*(?<amount>-?\d+)\s*,\s*(?<basis>Today|Now)\(\)\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateAddRegex();

    [GeneratedRegex(
        @"^=DateSerial\(\s*Year\(Today\(\)\)\s*,\s*Month\(Today\(\)\)\s*,\s*1\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateSerialFirstDayRegex();

    private sealed record CachedDefinition(DateTime Modified, DynamicReportDefinition Definition);
}
