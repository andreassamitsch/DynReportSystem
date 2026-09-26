namespace DynReportSystem.Models;

public sealed class ProductionTvDashboard
{
    public List<ProductionTvDepartment> Departments { get; init; } = [];
    public int MachineCount { get; init; }
    public int ProductionCount { get; init; }
    public int SetupCount { get; init; }
    public int WarningCount { get; init; }
    public decimal UnplannedDowntimeHours { get; init; }
}

public sealed class ProductionTvDepartment
{
    public string Name { get; init; } = "";
    public List<ProductionTvCard> Cards { get; init; } = [];
}

public sealed class ProductionTvCard
{
    public string Department { get; init; } = "";
    public string Machine { get; init; } = "";
    public string Customer { get; init; } = "";
    public string Article { get; init; } = "";
    public string ArticleNumber { get; init; } = "";
    public string Order { get; init; } = "";
    public string OperationNumber { get; init; } = "";
    public string Operation { get; init; } = "";
    public string Status { get; init; } = "";
    public string StatusColor { get; init; } = "#fbff5e";
    public string StatusTextColor { get; init; } = "#263d45";
    public DateTime? StatusSince { get; init; }
    public DateTime? PlannedEnd { get; init; }
    public int OperationStatus { get; init; }
    public string OperationStatusIcon { get; init; } = "●";
    public string Operators { get; init; } = "";
    public string Inspection { get; init; } = "";
    public int InspectionState { get; init; }
    public string InspectionClass { get; init; } = "neutral";
    public ProductionTvMetric Setup { get; init; } = new();
    public ProductionTvMetric ShiftCycle { get; init; } = new();
    public ProductionTvMetric ShiftQuantity { get; init; } = new();
    public ProductionTvMetric TotalCycle { get; init; } = new();
    public ProductionTvMetric ContainerCycle { get; init; } = new();
    public DateTime? LastContainerAt { get; init; }
    public decimal? LastContainerQuantity { get; init; }
    public string LastContainerReporter { get; init; } = "";
    public decimal UnplannedDowntimeHours { get; init; }
    public bool ContainerOverdue { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public sealed class ProductionTvMetric
{
    public decimal? Actual { get; init; }
    public decimal? Target { get; init; }
    public string Unit { get; init; } = "";
    public string Health { get; init; } = "neutral";
    public string ActualText { get; init; } = "–";
    public string TargetText { get; init; } = "–";
}
