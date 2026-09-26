using System.Text.Json;

namespace DynReportSystem.Models;

public sealed class DynReportManifest
{
    public string SchemaVersion { get; set; } = "2.0";
    public string ReportId { get; set; } = "";
    public string Path { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public List<string> Tags { get; set; } = [];
    public List<DynReportAccessGrant> Grants { get; set; } = [];
}

public sealed class DynReportAccessGrant
{
    public string PrincipalType { get; set; } = "WindowsPrincipal";
    public string Principal { get; set; } = "";
    public List<string> Permissions { get; set; } = [];
}

public sealed class DynReportDocument
{
    public DynReportSettings Settings { get; set; } = new();
    public List<DynReportDataSource> DataSources { get; set; } = [];
    public List<DynReportDataset> Datasets { get; set; } = [];
    public List<DynReportParameter> Parameters { get; set; } = [];
    public Dictionary<string, DynExpr> Calculations { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<DynReportPage> Pages { get; set; } = [];
}

public sealed class DynReportSettings
{
    public bool AutoRun { get; set; }
    public int AutoRefreshSeconds { get; set; }
    public bool ParametersCollapsedByDefault { get; set; }
    public string Culture { get; set; } = "de-AT";
    public int MaxRowsPerDataset { get; set; } = 20000;
    public int CommandTimeoutSeconds { get; set; } = 120;
}

public sealed class DynReportDataSource
{
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "SQL";
    public string ConfigKey { get; set; } = "";
    public string DefaultConnectionString { get; set; } = "";
}

public sealed class DynReportDataset
{
    public string Id { get; set; } = "";
    public string DataSourceId { get; set; } = "";
    public string CommandType { get; set; } = "Text";
    public string QueryFile { get; set; } = "";
    public List<DynReportQueryParameter> Parameters { get; set; } = [];
    public List<string> Fields { get; set; } = [];
}

public sealed class DynReportQueryParameter
{
    public string SqlName { get; set; } = "";
    public string ParameterName { get; set; } = "";
    public string DataType { get; set; } = "String";
    public bool MultiValue { get; set; }
}

public sealed class DynReportParameter
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string DataType { get; set; } = "String";
    public string InputMode { get; set; } = "";
    public bool MultiValue { get; set; }
    public bool Nullable { get; set; }
    public bool Hidden { get; set; }
    public int Order { get; set; }
    public List<string> DefaultValues { get; set; } = [];
    public List<DynReportParameterOption> Options { get; set; } = [];
    public string OptionsDataset { get; set; } = "";
    public string OptionsValueField { get; set; } = "";
    public string OptionsLabelField { get; set; } = "";
}

public sealed record DynReportParameterOption(string Value, string Label);

public sealed class DynReportPage
{
    public string Id { get; set; } = "main";
    public string Title { get; set; } = "";
    public int Columns { get; set; } = 12;
    public List<DynVisual> Components { get; set; } = [];
}

public sealed class DynVisual
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Dataset { get; set; } = "";
    public int Span { get; set; } = 12;
    public int MobileSpan { get; set; } = 12;
    public string CssClass { get; set; } = "";
    public DynExpr? Value { get; set; }
    public string Format { get; set; } = "";
    public string Unit { get; set; } = "";
    public List<DynMetricItem> Metrics { get; set; } = [];
    public DynGroupedBoard? Board { get; set; }
}

public sealed class DynMetricItem
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public DynExpr Value { get; set; } = new();
    public string Format { get; set; } = "";
    public string Unit { get; set; } = "";
    public List<DynConditionalRule> Rules { get; set; } = [];
}

public sealed class DynGroupedBoard
{
    public List<string> SectionGroupBy { get; set; } = [];
    public List<string> RowGroupBy { get; set; } = [];
    public List<DynSortDefinition> RowSort { get; set; } = [];
    public List<DynBoardColumn> Columns { get; set; } = [];
    public List<DynLegendItem> Legend { get; set; } = [];
    public List<DynWarningDefinition> Warnings { get; set; } = [];
}

public sealed class DynBoardColumn
{
    public string Id { get; set; } = "";
    public string Header { get; set; } = "";
    public string Kind { get; set; } = "text";
    public string Width { get; set; } = "1fr";
    public DynExpr? Value { get; set; }
    public DynExpr? Secondary { get; set; }
    public DynExpr? Tertiary { get; set; }
    public DynExpr? Target { get; set; }
    public string Format { get; set; } = "";
    public string TargetFormat { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Direction { get; set; } = "LowerIsBetter";
    public Dictionary<string, string> ColorMap { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DynStatePresentation> StateMap { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<DynConditionalRule> Rules { get; set; } = [];
}

public sealed class DynStatePresentation
{
    public string Label { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Tone { get; set; } = "neutral";
    public string Color { get; set; } = "";
}

public sealed class DynConditionalRule
{
    public DynExpr When { get; set; } = new();
    public string Tone { get; set; } = "";
    public string Color { get; set; } = "";
    public string TextColor { get; set; } = "";
}

public sealed class DynWarningDefinition
{
    public string Text { get; set; } = "";
    public DynExpr When { get; set; } = new();
}

public sealed class DynLegendItem
{
    public string Label { get; set; } = "";
    public string Color { get; set; } = "";
}

public sealed class DynSortDefinition
{
    public string Field { get; set; } = "";
    public string Direction { get; set; } = "Asc";
}

public sealed class DynExpr
{
    public string Op { get; set; } = "const";
    public string Field { get; set; } = "";
    public string Name { get; set; } = "";
    public string Parameter { get; set; } = "";
    public JsonElement Value { get; set; }
    public List<DynExpr> Args { get; set; } = [];
    public DynExpr? Where { get; set; }
    public List<string> DistinctBy { get; set; } = [];
    public List<string> GroupBy { get; set; } = [];
    public List<DynSortDefinition> OrderBy { get; set; } = [];
    public string Separator { get; set; } = ", ";
}

public sealed class DynReportParameterValue
{
    public string Name { get; init; } = "";
    public string DataType { get; init; } = "String";
    public bool MultiValue { get; init; }
    public List<string> Values { get; } = [];
}

public sealed class DynReportRun
{
    public Dictionary<string, QueryResult> Results { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<string> Errors { get; init; } = [];
}

public sealed class LoadedDynReportPackage
{
    public required DynReportManifest Manifest { get; init; }
    public required DynReportDocument Document { get; init; }
    public required string FilePath { get; init; }
    public required DateTime ModifiedUtc { get; init; }
    public required IReadOnlyDictionary<string, string> TextFiles { get; init; }
}

public sealed record DynReportRevision(
    string ReportId,
    string Version,
    string FilePath,
    DateTime CreatedUtc);
