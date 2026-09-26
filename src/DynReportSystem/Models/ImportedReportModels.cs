namespace DynReportSystem.Models;

public sealed class ImportedPortalCatalogModel
{
    public string Source { get; set; } = "";
    public ImportedPortalStats Stats { get; set; } = new();
    public List<ImportedFolder> Folders { get; set; } = [];
    public List<ImportedReport> Reports { get; set; } = [];
    public List<ImportedSharedDataset> SharedDatasets { get; set; } = [];
    public List<ImportedDataSource> DataSources { get; set; } = [];
    public List<ImportedLegacyItem> LegacyItems { get; set; } = [];
}

public sealed class ImportedPortalStats
{
    public int Folders { get; set; }
    public int Reports { get; set; }
    public int SharedDatasets { get; set; }
    public int SharedDataSources { get; set; }
    public int LegacyItems { get; set; }
    public int Subscriptions { get; set; }
    public int Schedules { get; set; }
}

public sealed class ImportedFolder
{
    public string Id { get; set; } = "";
    public string? ItemId { get; set; }
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string? ParentId { get; set; }
    public bool Hidden { get; set; }
}

public sealed class ImportedReport
{
    public string Id { get; set; } = "";
    public string? ItemId { get; set; }
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string FolderId { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Hidden { get; set; }
    public string? RdlPath { get; set; }
    public string? SourcePath { get; set; }
    public string? SpecialUrl { get; set; }
    public string LegacyType { get; set; } = "Report";
}

public sealed class ImportedSharedDataset
{
    public string? ItemId { get; set; }
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string? DefinitionPath { get; set; }
}

public sealed class ImportedDataSource
{
    public string? ItemId { get; set; }
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string? DefinitionPath { get; set; }
    public string Provider { get; set; } = "SQL";
    public string OriginalConnectString { get; set; } = "";
    public string ConfigKey { get; set; } = "";
}

public sealed class ImportedLegacyItem
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public int Type { get; set; }
}

public sealed class DynamicReportDefinition
{
    public required ImportedReport Report { get; init; }
    public required IReadOnlyList<DynamicReportParameter> Parameters { get; init; }
    public required IReadOnlyDictionary<string, DynamicDataSourceDefinition> DataSources { get; init; }
    public required IReadOnlyDictionary<string, DynamicDatasetDefinition> DataSets { get; init; }
    public required IReadOnlySet<string> BodyDataSets { get; init; }
}

public sealed class DynamicDataSourceDefinition
{
    public string Name { get; init; } = "";
    public string? Reference { get; init; }
    public string Provider { get; init; } = "SQL";
    public string? ConnectString { get; init; }
}

public sealed class DynamicDatasetDefinition
{
    public string Name { get; init; } = "";
    public string? DataSourceName { get; init; }
    public string? DataSourceReference { get; init; }
    public string CommandText { get; init; } = "";
    public string CommandType { get; init; } = "Text";
    public bool IsShared { get; init; }
    public string? SharedDataSetReference { get; init; }
    public IReadOnlyList<DynamicQueryBinding> Bindings { get; init; } = [];
    public IReadOnlyList<string> Fields { get; init; } = [];
}

public sealed record DynamicQueryBinding(
    string SqlName,
    string? ReportParameter,
    string Expression);

public sealed class DynamicReportParameter
{
    public string Name { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string DataType { get; init; } = "String";
    public bool Hidden { get; init; }
    public bool MultiValue { get; init; }
    public bool Nullable { get; init; }
    public bool AllowBlank { get; init; }
    public IReadOnlyList<string> DefaultExpressions { get; init; } = [];
    public DynamicParameterDataSetReference? DefaultDataSet { get; init; }
    public IReadOnlyList<DynamicParameterOption> StaticValidValues { get; init; } = [];
    public DynamicParameterDataSetReference? ValidValuesDataSet { get; init; }
}

public sealed record DynamicParameterDataSetReference(
    string DataSetName,
    string ValueField,
    string? LabelField);

public sealed record DynamicParameterOption(string Value, string Label);

public sealed class DynamicParameterValue
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "String";
    public bool MultiValue { get; set; }
    public List<string> Values { get; set; } = [];
}

public sealed class DynamicReportRun
{
    public Dictionary<string, QueryResult> Results { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<string> Errors { get; init; } = [];
}

public sealed record AutoVisualization(
    string Type,
    string Title,
    object Options);
