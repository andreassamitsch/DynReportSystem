using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

/// <summary>
/// Extracts the semantic presentation from the original SSRS RDL. DynReport
/// intentionally does not reproduce the paper canvas pixel-for-pixel, but it
/// preserves which tables/charts/gauges were shown, their order and relative
/// size, original column selection/order, grouping, chart category/series
/// expressions, stacking and report-defined colors.
/// </summary>
public sealed partial class RdlPresentationService(ImportedPortalCatalog portal)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CachedPresentation> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public RdlPresentationDefinition GetPresentation(string reportId)
    {
        var report = portal.GetReport(reportId);
        var source = ResolveSourceReport(report);

        if (string.IsNullOrWhiteSpace(source.RdlPath))
            return new RdlPresentationDefinition { ReportId = reportId };

        var fullPath = portal.ResolveContentPath(source.RdlPath);
        if (!File.Exists(fullPath))
            return new RdlPresentationDefinition { ReportId = reportId };

        var modified = File.GetLastWriteTimeUtc(fullPath);

        lock (_gate)
        {
            if (_cache.TryGetValue(reportId, out var cached) && cached.Modified == modified)
                return cached.Definition;

            var definition = Parse(reportId, fullPath);
            _cache[reportId] = new CachedPresentation(modified, definition);
            return definition;
        }
    }

    private ImportedReport ResolveSourceReport(ImportedReport report)
    {
        if (!string.IsNullOrWhiteSpace(report.RdlPath))
            return report;

        if (string.IsNullOrWhiteSpace(report.SourcePath))
            return report;

        return portal.Catalog.Reports.FirstOrDefault(x =>
            ImportedPortalCatalog.NormalizePath(x.Path).Equals(
                ImportedPortalCatalog.NormalizePath(report.SourcePath),
                StringComparison.OrdinalIgnoreCase)) ?? report;
    }

    private RdlPresentationDefinition Parse(string reportId, string fullPath)
    {
        var doc = LoadXml(fullPath);
        var root = doc.Root ?? throw new InvalidDataException("RDL ist leer.");
        var ns = root.Name.Namespace;

        // RDL 2016+ stores the printable body below
        // ReportSections/ReportSection. Older RDL versions may still expose
        // Body directly below Report, so support both layouts.
        var reportSection = root
            .Element(ns + "ReportSections")?
            .Elements(ns + "ReportSection")
            .FirstOrDefault();

        var body = root.Element(ns + "Body")
                   ?? reportSection?.Element(ns + "Body");

        var bodyWidth = UnitToPoints(Text(reportSection, ns + "Width"))
                        ?? UnitToPoints(Text(body, ns + "Width"))
                        ?? UnitToPoints(Text(root, ns + "Width"))
                        ?? 720d;

        var colors = ParseCodeColors(Text(root, ns + "Code") ?? "");
        var items = new List<RdlPresentationItem>();
        var order = 0;

        var reportItems = body?.Element(ns + "ReportItems");
        if (reportItems is not null)
            ParseReportItems(reportItems, ns, bodyWidth, 0, 0, null, colors, items, ref order);

        return new RdlPresentationDefinition
        {
            ReportId = reportId,
            BodyWidthPt = bodyWidth,
            Items = items
                .OrderBy(x => x.TopPt)
                .ThenBy(x => x.LeftPt)
                .ThenBy(x => x.Order)
                .ToArray(),
            ColorMap = colors
        };
    }

    private void ParseReportItems(
        XElement reportItems,
        XNamespace ns,
        double bodyWidth,
        double topOffset,
        double leftOffset,
        string? sectionTitle,
        IReadOnlyDictionary<string, string> colorMap,
        List<RdlPresentationItem> output,
        ref int order)
    {
        foreach (var item in reportItems.Elements())
        {
            var kind = item.Name.LocalName;
            var top = topOffset + (UnitToPoints(Text(item, ns + "Top")) ?? 0);
            var left = leftOffset + (UnitToPoints(Text(item, ns + "Left")) ?? 0);
            var width = UnitToPoints(Text(item, ns + "Width")) ?? bodyWidth;
            var height = UnitToPoints(Text(item, ns + "Height")) ?? 120;
            var name = item.Attribute("Name")?.Value ?? $"{kind}{order + 1}";

            if (kind.Equals("Rectangle", StringComparison.OrdinalIgnoreCase))
            {
                var nested = item.Element(ns + "ReportItems");
                if (nested is null)
                    continue;

                var title = FindContainerTitle(nested, ns) ?? sectionTitle;
                ParseReportItems(
                    nested,
                    ns,
                    bodyWidth,
                    top,
                    left,
                    title,
                    colorMap,
                    output,
                    ref order);

                continue;
            }

            if (kind.Equals("Textbox", StringComparison.OrdinalIgnoreCase))
            {
                var text = TextboxValue(item, ns);
                if (!ShouldRenderHeading(item, text, ns))
                    continue;

                output.Add(new RdlPresentationItem
                {
                    Id = name,
                    Kind = "Heading",
                    Title = text!,
                    Text = text,
                    TopPt = top,
                    LeftPt = left,
                    WidthPt = width,
                    HeightPt = height,
                    ColumnSpan = 12,
                    Order = order++,
                    BackgroundColor = StyleValue(item, ns, "BackgroundColor"),
                    ForegroundColor = StyleValue(item, ns, "Color")
                });
                continue;
            }

            if (kind.Equals("Tablix", StringComparison.OrdinalIgnoreCase))
            {
                var dataSet = Text(item, ns + "DataSetName");
                var table = ParseTablix(item, ns);
                var nearby = FindNearbyTitle(reportItems, item, ns);
                var title = nearby ?? sectionTitle ?? HumanizeName(name);

                output.Add(new RdlPresentationItem
                {
                    Id = name,
                    Kind = "Tablix",
                    Title = title,
                    Subtitle = BuildTableSubtitle(table),
                    DataSetName = dataSet,
                    TopPt = top,
                    LeftPt = left,
                    WidthPt = width,
                    HeightPt = height,
                    ColumnSpan = Span(width, bodyWidth, minimum: 6),
                    Order = order++,
                    Table = table,
                    Filters = ParseFilters(item, ns),
                    HiddenExpression = ParseHiddenExpression(item, ns),
                    BackgroundColor = StyleValue(item, ns, "BackgroundColor")
                });
                continue;
            }

            if (kind.Equals("Chart", StringComparison.OrdinalIgnoreCase))
            {
                var chart = ParseChart(item, ns);
                var title = ChartTitle(item, ns)
                            ?? FindNearbyTitle(reportItems, item, ns)
                            ?? sectionTitle
                            ?? HumanizeName(name);

                output.Add(new RdlPresentationItem
                {
                    Id = name,
                    Kind = "Chart",
                    Title = title,
                    DataSetName = Text(item, ns + "DataSetName"),
                    TopPt = top,
                    LeftPt = left,
                    WidthPt = width,
                    HeightPt = height,
                    ColumnSpan = Span(width, bodyWidth, minimum: 4),
                    Order = order++,
                    Chart = chart,
                    Filters = ParseFilters(item, ns),
                    HiddenExpression = ParseHiddenExpression(item, ns),
                    BackgroundColor = StyleValue(item, ns, "BackgroundColor")
                });
                continue;
            }

            if (kind.Equals("GaugePanel", StringComparison.OrdinalIgnoreCase))
            {
                var gauge = ParseGauge(item, ns);
                var title = FindNearbyTitle(reportItems, item, ns)
                            ?? sectionTitle
                            ?? HumanizeName(name);

                output.Add(new RdlPresentationItem
                {
                    Id = name,
                    Kind = "Gauge",
                    Title = title,
                    DataSetName = Text(item, ns + "DataSetName"),
                    TopPt = top,
                    LeftPt = left,
                    WidthPt = width,
                    HeightPt = height,
                    ColumnSpan = Span(width, bodyWidth, minimum: 3),
                    Order = order++,
                    Gauge = gauge,
                    Filters = ParseFilters(item, ns),
                    HiddenExpression = ParseHiddenExpression(item, ns),
                    BackgroundColor = StyleValue(item, ns, "BackgroundColor")
                });
            }
        }
    }

    private static RdlTablePresentation ParseTablix(XElement tablix, XNamespace ns)
    {
        var body = tablix.Element(ns + "TablixBody");
        var widths = body?.Element(ns + "TablixColumns")?
            .Elements(ns + "TablixColumn")
            .Select(x => UnitToPoints(Text(x, ns + "Width")) ?? 80)
            .ToArray() ?? [];

        var rows = body?.Element(ns + "TablixRows")?
            .Elements(ns + "TablixRow")
            .ToArray() ?? [];

        var maxColumns = rows
            .Select(row => row.Element(ns + "TablixCells")?.Elements(ns + "TablixCell").Count() ?? 0)
            .DefaultIfEmpty(0)
            .Max();

        var columns = new List<RdlTableColumnPresentation>();

        for (var columnIndex = 0; columnIndex < maxColumns; columnIndex++)
        {
            string? header = null;
            string? field = null;
            string? expression = null;
            string? format = null;

            foreach (var row in rows)
            {
                var cell = row.Element(ns + "TablixCells")?
                    .Elements(ns + "TablixCell")
                    .ElementAtOrDefault(columnIndex);

                if (cell is null)
                    continue;

                var textbox = cell.Descendants(ns + "Textbox").FirstOrDefault();
                if (textbox is null)
                    continue;

                var value = TextboxValue(textbox, ns)?.Trim();
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var match = FieldReferenceRegex().Match(value);
                if (match.Success)
                {
                    field ??= match.Groups["field"].Value;
                    expression ??= value;
                    format ??= TextboxFormat(textbox, ns);
                    continue;
                }

                if (field is null && !value.StartsWith('=') && value.Length <= 120)
                    header = value;
            }

            if (string.IsNullOrWhiteSpace(field))
                continue;

            columns.Add(new RdlTableColumnPresentation
            {
                Header = string.IsNullOrWhiteSpace(header) ? HumanizeName(field) : header,
                Field = field,
                Expression = expression ?? $"=Fields!{field}.Value",
                Format = format,
                WidthPt = columnIndex < widths.Length ? widths[columnIndex] : 90
            });
        }

        var groupFields = tablix
            .Descendants(ns + "TablixRowHierarchy")
            .Descendants(ns + "GroupExpression")
            .Select(x => FieldFromExpression(x.Value))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();

        var sortFields = tablix
            .Descendants(ns + "SortExpression")
            .Select(x => FieldFromExpression(Text(x, ns + "Value") ?? x.Value))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();

        return new RdlTablePresentation
        {
            Columns = columns,
            GroupFields = groupFields,
            SortFields = sortFields
        };
    }

    private static RdlChartPresentation ParseChart(XElement chart, XNamespace ns)
    {
        var categoryExpression = chart
            .Element(ns + "ChartCategoryHierarchy")?
            .Descendants(ns + "GroupExpression")
            .Select(x => x.Value.Trim())
            .FirstOrDefault();

        var seriesGroupExpression = chart
            .Element(ns + "ChartSeriesHierarchy")?
            .Descendants(ns + "GroupExpression")
            .Select(x => x.Value.Trim())
            .FirstOrDefault();

        var series = new List<RdlChartSeriesPresentation>();

        foreach (var chartSeries in chart.Element(ns + "ChartData")?.Elements(ns + "ChartSeries") ?? [])
        {
            var valueExpression = chartSeries
                .Descendants(ns + "ChartDataPointValues")
                .SelectMany(x => x.Elements())
                .Select(x => x.Value.Trim())
                .FirstOrDefault(x => x.Length > 0) ?? "";

            var (aggregate, field) = AggregateAndField(valueExpression);
            var style = chartSeries.Element(ns + "Style");
            var dataPointStyle = chartSeries
                .Descendants(ns + "ChartDataPoint")
                .Select(x => x.Element(ns + "Style"))
                .FirstOrDefault(x => x is not null);

            var colorExpression = Text(style, ns + "Color")
                                  ?? Text(dataPointStyle, ns + "Color");

            var staticColor = colorExpression is not null
                              && !colorExpression.TrimStart().StartsWith('=')
                ? colorExpression.Trim()
                : null;

            series.Add(new RdlChartSeriesPresentation
            {
                Name = chartSeries.Attribute("Name")?.Value ?? field ?? "Wert",
                Type = Text(chartSeries, ns + "Type") ?? "Column",
                Subtype = Text(chartSeries, ns + "Subtype"),
                ValueField = field,
                ValueExpression = valueExpression,
                Aggregate = aggregate,
                Color = staticColor,
                ColorExpression = colorExpression
            });
        }

        var legend = chart.Element(ns + "ChartLegends")?.Elements(ns + "ChartLegend").FirstOrDefault();
        var legendHidden = Bool(Text(legend, ns + "Hidden"));

        var customPalette = chart.Element(ns + "ChartCustomPaletteColors")?
            .Elements(ns + "ChartCustomPaletteColor")
            .Select(x => x.Value.Trim())
            .Where(x => x.Length > 0)
            .ToArray() ?? [];

        return new RdlChartPresentation
        {
            ChartType = series.FirstOrDefault()?.Type ?? "Column",
            Subtype = series.FirstOrDefault()?.Subtype,
            CategoryField = FieldFromExpression(categoryExpression),
            CategoryExpression = categoryExpression,
            SeriesGroupField = FieldFromExpression(seriesGroupExpression),
            SeriesGroupExpression = seriesGroupExpression,
            ShowLegend = !legendHidden,
            Palette = Text(chart, ns + "Palette"),
            CustomPalette = customPalette,
            Series = series
        };
    }

    private static RdlGaugePresentation ParseGauge(XElement gaugePanel, XNamespace ns)
    {
        var radial = gaugePanel.Descendants(ns + "RadialGauge").Any();
        var input = gaugePanel.Descendants(ns + "GaugeInputValue").FirstOrDefault();
        var expression = Text(input, ns + "Value") ?? "";
        var (aggregate, field) = AggregateAndField(expression);

        double? ParseScaleValue(string containerName)
        {
            var container = gaugePanel.Descendants(ns + containerName).FirstOrDefault();
            var value = container?.Descendants(ns + "Value").Select(x => x.Value).FirstOrDefault();
            return double.TryParse(value?.TrimStart('='), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        return new RdlGaugePresentation
        {
            GaugeType = radial ? "Radial" : "Linear",
            ValueField = field,
            ValueExpression = expression,
            Aggregate = aggregate,
            Minimum = ParseScaleValue("MinimumValue"),
            Maximum = ParseScaleValue("MaximumValue")
        };
    }

    private static string? ParseHiddenExpression(XElement item, XNamespace ns)
    {
        var visibility = item.Element(ns + "Visibility");
        var hidden = Text(visibility, ns + "Hidden")?.Trim();
        return string.IsNullOrWhiteSpace(hidden) ? null : hidden;
    }

    private static IReadOnlyList<RdlFilterPresentation> ParseFilters(XElement item, XNamespace ns)
    {
        var filters = new List<RdlFilterPresentation>();

        foreach (var filter in item.Element(ns + "Filters")?.Elements(ns + "Filter") ?? [])
        {
            var expression = Text(filter, ns + "FilterExpression") ?? "";
            var field = FieldFromExpression(expression);
            if (string.IsNullOrWhiteSpace(field))
                continue;

            var op = Text(filter, ns + "Operator") ?? "Equal";
            var values = filter.Element(ns + "FilterValues")?
                .Elements(ns + "FilterValue")
                .Select(x => x.Value.Trim())
                .Where(x => x.Length > 0)
                .ToArray() ?? [];

            filters.Add(new RdlFilterPresentation(field, op, values));
        }

        return filters;
    }

    private static IReadOnlyDictionary<string, string> ParseCodeColors(string code)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in CodeColorRegex().Matches(code))
        {
            var key = match.Groups["key"].Value.Trim();
            var color = match.Groups["color"].Value.Trim();
            if (key.Length > 0 && color.Length > 0)
                map[key] = color;
        }

        return map;
    }

    private static string? FindContainerTitle(XElement reportItems, XNamespace ns) =>
        reportItems.Elements(ns + "Textbox")
            .Select(x => TextboxValue(x, ns))
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !x.TrimStart().StartsWith('='));

    private static string? FindNearbyTitle(XElement reportItems, XElement visual, XNamespace ns)
    {
        var top = UnitToPoints(Text(visual, ns + "Top")) ?? 0;
        var left = UnitToPoints(Text(visual, ns + "Left")) ?? 0;
        var width = UnitToPoints(Text(visual, ns + "Width")) ?? 400;

        return reportItems.Elements(ns + "Textbox")
            .Select(box => new
            {
                Box = box,
                Text = TextboxValue(box, ns)?.Trim(),
                Top = UnitToPoints(Text(box, ns + "Top")) ?? 0,
                Left = UnitToPoints(Text(box, ns + "Left")) ?? 0,
                Width = UnitToPoints(Text(box, ns + "Width")) ?? 100
            })
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Text)
                && !x.Text!.StartsWith('=')
                && x.Top <= top + 3
                && top - x.Top <= 55
                && x.Left < left + width
                && x.Left + x.Width > left)
            .OrderByDescending(x => x.Top)
            .Select(x => x.Text)
            .FirstOrDefault();
    }

    private static bool ShouldRenderHeading(XElement textbox, string? text, XNamespace ns)
    {
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('='))
            return false;

        var fontSize = UnitToPoints(StyleValue(textbox, ns, "FontSize")) ?? 0;
        var weight = StyleValue(textbox, ns, "FontWeight");
        var bold = string.Equals(weight, "Bold", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(weight, "700", StringComparison.OrdinalIgnoreCase);

        return fontSize >= 12 || (bold && fontSize >= 10);
    }

    private static string? ChartTitle(XElement chart, XNamespace ns)
    {
        foreach (var title in chart.Element(ns + "ChartTitles")?.Elements(ns + "ChartTitle") ?? [])
        {
            var caption = Text(title, ns + "Caption")?.Trim();
            if (!string.IsNullOrWhiteSpace(caption) && !caption.StartsWith('='))
                return caption;
        }

        return null;
    }

    private static string? TextboxValue(XElement textbox, XNamespace ns)
    {
        var direct = Text(textbox, ns + "Value");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        return textbox
            .Descendants(ns + "TextRun")
            .Select(x => Text(x, ns + "Value"))
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    private static string? TextboxFormat(XElement textbox, XNamespace ns) =>
        textbox
            .Descendants(ns + "TextRun")
            .Select(x => x.Element(ns + "Style"))
            .Where(x => x is not null)
            .Select(x => Text(x, ns + "Format"))
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
        ?? Text(textbox.Element(ns + "Style"), ns + "Format");

    private static string? StyleValue(XElement element, XNamespace ns, string name) =>
        Text(element.Element(ns + "Style"), ns + name);

    private static string? BuildTableSubtitle(RdlTablePresentation table)
    {
        if (table.GroupFields.Count == 0)
            return table.Columns.Count > 0 ? $"{table.Columns.Count} Originalspalten" : null;

        return $"Gruppierung: {string.Join(" › ", table.GroupFields)}";
    }

    private static (string Aggregate, string? Field) AggregateAndField(string expression)
    {
        var aggregate = AggregateRegex().Match(expression);
        if (aggregate.Success)
            return (
                CultureInfo.InvariantCulture.TextInfo.ToTitleCase(aggregate.Groups["agg"].Value.ToLowerInvariant()),
                aggregate.Groups["field"].Value);

        return ("Sum", FieldFromExpression(expression));
    }

    private static string? FieldFromExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var match = FieldReferenceRegex().Match(expression);
        return match.Success ? match.Groups["field"].Value : null;
    }

    private static int Span(double width, double bodyWidth, int minimum)
    {
        if (bodyWidth <= 0)
            return 12;

        var span = (int)Math.Round(width / bodyWidth * 12d);
        return Math.Clamp(span, minimum, 12);
    }

    private static double? UnitToPoints(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = UnitRegex().Match(value.Trim());
        if (!match.Success)
            return null;

        var number = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "in" => number * 72d,
            "cm" => number / 2.54d * 72d,
            "mm" => number / 25.4d * 72d,
            "pt" => number,
            "pc" => number * 12d,
            _ => number
        };
    }

    private static string HumanizeName(string value) =>
        Regex.Replace(value, @"(?<=[a-z0-9])(?=[A-Z])", " ").Replace("_", " ").Trim();

    private static string? Text(XElement? parent, XName name) =>
        parent?.Element(name)?.Value;

    private static bool Bool(string? value) =>
        value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

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

    [GeneratedRegex(@"Fields!(?<field>[^.]+)\.Value", RegexOptions.IgnoreCase)]
    private static partial Regex FieldReferenceRegex();

    [GeneratedRegex(@"(?<agg>Sum|Avg|Average|Count|Min|Max|First|Last)\s*\(\s*Fields!(?<field>[^.]+)\.Value", RegexOptions.IgnoreCase)]
    private static partial Regex AggregateRegex();

    [GeneratedRegex(@"Case\s+""(?<key>[^""]+)""(?:(?!\bCase\b).){0,400}?""(?<color>#[0-9A-Fa-f]{6})""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CodeColorRegex();

    [GeneratedRegex(@"^(?<value>-?[0-9]+(?:\.[0-9]+)?)\s*(?<unit>in|cm|mm|pt|pc)?$", RegexOptions.IgnoreCase)]
    private static partial Regex UnitRegex();

    private sealed record CachedPresentation(DateTime Modified, RdlPresentationDefinition Definition);
}
