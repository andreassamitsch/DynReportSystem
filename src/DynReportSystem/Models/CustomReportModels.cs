using System.Text.Json.Serialization;

namespace DynReportSystem.Models;

/// <summary>
/// Portable report presentation file created after manual report analysis.
/// It references the existing migrated SSRS report for data/parameters and
/// replaces only the DynReport presentation layer.
/// </summary>
public sealed class CustomReportDefinition
{
    public string SchemaVersion { get; set; } = "1.0";
    public string TargetReportId { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Renderer { get; set; } = "";
    public string PrimaryDataset { get; set; } = "";
    public bool AutoRun { get; set; }
    public int AutoRefreshSeconds { get; set; }
    public bool ParametersCollapsedByDefault { get; set; }
    public List<CustomParameterPresentation> Parameters { get; set; } = [];
    public ProductionTvDefinition? ProductionTv { get; set; }
}

public sealed class CustomParameterPresentation
{
    public string Name { get; set; } = "";
    public string? Label { get; set; }
    public int Order { get; set; }
    public string? InputMode { get; set; }
    public bool? Hidden { get; set; }
}

public sealed class ProductionTvDefinition
{
    public string DepartmentField { get; set; } = "MasAbteilung";
    public string MachineField { get; set; } = "MaschineID";
    public string CustomerField { get; set; } = "Kunde";
    public string ArticleField { get; set; } = "Artikel";
    public string ArticleNumberField { get; set; } = "ArtikelNr";
    public string OrderField { get; set; } = "Auftrag";
    public string OperationNumberField { get; set; } = "AGNr";
    public string OperationField { get; set; } = "Vorgang";
    public string StatusField { get; set; } = "Stillstand";
    public string StatusBeginField { get; set; } = "BEGINDATE";
    public string StatusEndField { get; set; } = "ENDDATE";
    public string OperationStatusField { get; set; } = "AGStatus";
    public string OperatorField { get; set; } = "Bediener";
    public string OperatorActiveField { get; set; } = "BedAktiv";
    public string InspectionField { get; set; } = "EMpruefung";
    public string InspectionStateField { get; set; } = "EMpruefungState";
    public string SetupActualField { get; set; } = "RüstzeitIst";
    public string SetupTargetField { get; set; } = "RüstzeitSoll";
    public string CycleActualField { get; set; } = "TaktzeitIst";
    public string CycleTargetField { get; set; } = "TaktzeitSoll";
    public string ContainerTargetField { get; set; } = "BehälterTaktSoll";
    public string ContainerElapsedField { get; set; } = "ProdDauerSeitBehälter";
    public string ProducedTotalField { get; set; } = "AG_ProduzierteMenge";
    public string ReleasedField { get; set; } = "RELEASEDCOUNT";
    public string ReleasedSumField { get; set; } = "SumRELEASEDCOUNT";
    public string ContainerKeyField { get; set; } = "CONTAINERKEY";
    public string ContainerFinishedField { get; set; } = "FINISHEDON";
    public string ContainerReporterField { get; set; } = "BhGemeldetVon";
    public string DurationField { get; set; } = "Duration";
    public string CurrentField { get; set; } = "ISCURRENT";
    public string UnplannedDowntimeField { get; set; } = "AG_ungeplanteStillstände";
    public string PlannedEndField { get; set; } = "NLZ_Ende";
    public int MinimumContainerTargetMinutes { get; set; } = 60;
    public Dictionary<string, string> StatusColors { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public string FallbackStatusColor { get; set; } = "#fbff5e";
}

public sealed record InstalledCustomReport(
    string TargetReportId,
    string TargetPath,
    string Title,
    string Renderer,
    string FilePath,
    DateTime ModifiedUtc);
