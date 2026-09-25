namespace DynReportSystem.Models;

public sealed class DashboardDefinition
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string DataSourceRdl { get; set; } = "";
    public List<DashboardWidget> Widgets { get; set; } = [];
}

public sealed class DashboardWidget
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Dataset { get; set; } = "";
    public string? ValueField { get; set; }
    public string? CategoryField { get; set; }
    public string? SecondaryValueField { get; set; }
    public string? Format { get; set; }
    public string? TimeBucket { get; set; }
    public string Sort { get; set; } = "";
    public string TrendMode { get; set; } = "";
    public int Limit { get; set; }
    public int Order { get; set; }
    public int ColSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
    public int Height { get; set; } = 340;
    public bool Horizontal { get; set; }
    public bool Interactive { get; set; } = true;
    public List<DashboardSeries> Series { get; set; } = [];
}

public sealed class DashboardSeries
{
    public string Name { get; set; } = "";
    public string Field { get; set; } = "";
    public string Type { get; set; } = "bar";
    public string Aggregate { get; set; } = "sum";
    public string Format { get; set; } = "number";
    public int Axis { get; set; }
    public bool Area { get; set; }
}
