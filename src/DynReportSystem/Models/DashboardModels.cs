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
    public string Dataset { get; set; } = "";
    public string? ValueField { get; set; }
    public string? CategoryField { get; set; }
    public string? SecondaryValueField { get; set; }
    public string? Format { get; set; }
    public int Order { get; set; }
    public int ColSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
    public bool Interactive { get; set; } = true;
}
