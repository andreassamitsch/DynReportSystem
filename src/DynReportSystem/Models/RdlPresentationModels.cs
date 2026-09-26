namespace DynReportSystem.Models;

public sealed class RdlPresentationDefinition
{
    public string ReportId { get; init; } = "";
    public double BodyWidthPt { get; init; } = 720;
    public IReadOnlyList<RdlPresentationItem> Items { get; init; } = [];
    public IReadOnlyDictionary<string, string> ColorMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool HasMeaningfulLayout => Items.Any(x =>
        x.Kind is "Chart" or "Tablix" or "Gauge" or "Heading");
}

public sealed class RdlPresentationItem
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    public string? DataSetName { get; init; }
    public double TopPt { get; init; }
    public double LeftPt { get; init; }
    public double WidthPt { get; init; }
    public double HeightPt { get; init; }
    public int ColumnSpan { get; init; } = 12;
    public int Order { get; init; }
    public RdlTablePresentation? Table { get; init; }
    public RdlChartPresentation? Chart { get; init; }
    public RdlGaugePresentation? Gauge { get; init; }
    public IReadOnlyList<RdlFilterPresentation> Filters { get; init; } = [];
    public string? HiddenExpression { get; init; }
    public string? Text { get; init; }
    public string? BackgroundColor { get; init; }
    public string? ForegroundColor { get; init; }
}

public sealed class RdlTablePresentation
{
    public IReadOnlyList<RdlTableColumnPresentation> Columns { get; init; } = [];
    public IReadOnlyList<string> GroupFields { get; init; } = [];
    public IReadOnlyList<string> SortFields { get; init; } = [];
    public bool HasRowGroups => GroupFields.Count > 0;
}

public sealed class RdlTableColumnPresentation
{
    public string Header { get; init; } = "";
    public string Field { get; init; } = "";
    public string Expression { get; init; } = "";
    public string? Format { get; init; }
    public double WidthPt { get; init; }
}

public sealed class RdlChartPresentation
{
    public string ChartType { get; init; } = "Column";
    public string? Subtype { get; init; }
    public string? CategoryField { get; init; }
    public string? CategoryExpression { get; init; }
    public string? SeriesGroupField { get; init; }
    public string? SeriesGroupExpression { get; init; }
    public bool ShowLegend { get; init; } = true;
    public string? Palette { get; init; }
    public IReadOnlyList<string> CustomPalette { get; init; } = [];
    public IReadOnlyList<RdlChartSeriesPresentation> Series { get; init; } = [];
}

public sealed class RdlChartSeriesPresentation
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "Column";
    public string? Subtype { get; init; }
    public string? ValueField { get; init; }
    public string ValueExpression { get; init; } = "";
    public string Aggregate { get; init; } = "Sum";
    public string? Color { get; init; }
    public string? ColorExpression { get; init; }
    public string? Format { get; init; }
}

public sealed record RdlFilterPresentation(
    string Field,
    string Operator,
    IReadOnlyList<string> Values);

public sealed class RdlGaugePresentation
{
    public string GaugeType { get; init; } = "Linear";
    public string? ValueField { get; init; }
    public string ValueExpression { get; init; } = "";
    public string Aggregate { get; init; } = "Sum";
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public string? Format { get; init; }
}

public sealed record RdlProjectedTable(
    QueryResult Result,
    IReadOnlyList<string> DisplayColumns,
    IReadOnlyList<string> GroupColumns,
    IReadOnlyList<string> SortColumns);

public sealed record RdlRenderedVisual(
    string Kind,
    string Title,
    object? Options = null,
    string? Value = null,
    string? Subtitle = null);
