using DynReportSystem.Models;
using DynReportSystem.Services;

static Dictionary<string, object?> TaskRow(
    string order, string position, string department,
    int openOrder, int openPosition, int openDepartment,
    string text, DateTime? finished, int sort)
{
    return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
    {
        ["Auftrag"] = order,
        ["Position"] = position,
        ["Abteilung"] = department,
        ["OffenAuftrag"] = openOrder,
        ["OffenGanzePosition"] = openPosition,
        ["OffenAbteilung"] = openDepartment,
        ["AuftragAnzeige"] = text,
        ["AbteilungSortNr"] = sort,
        ["LetztesAuftragDatum"] = finished
    };
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

var finished = new DateTime(2026, 10, 27, 14, 35, 0);
var rows = new List<Dictionary<string, object?>>
{
    TaskRow("AB-1", "10", "AV-Team", 3, 2, 2, "2 offen: Liefertermin", null, 1),
    TaskRow("AB-1", "10", "AV-Team", 3, 2, 2, "2 offen: Liefertermin", null, 1),
    TaskRow("AB-1", "20", "QS-Team", 3, 1, 1, "1 offen: Prüfung", null, 6),
    TaskRow("AB-1", "20", "Vertrieb", 3, 1, 0, "Erledigt", finished, 2),
    TaskRow("AB-2", "10", "Vertrieb", 0, 0, 0, "Erledigt", finished, 2),
    TaskRow("AB-12", "3", "Vertrieb", 0, 0, 0, "Erledigt", finished, 2)
};
var levels = new[]
{
    new DynHierarchyLevel
    {
        Field = "Auftrag", OpenCountField = "OffenAuftrag",
        CompletedDateField = "LetztesAuftragDatum",
        SummaryField = "AuftragAnzeige",
        SummaryLabelField = "Abteilung",
        SummaryDistinctField = "Abteilung",
        SummarySortField = "AbteilungSortNr",
        SummaryOpenCountField = "OffenAbteilung",
        SummaryOpenOnly = true
    },
    new DynHierarchyLevel { Field = "Position", OpenCountField = "OffenGanzePosition" }
};

var groups = DynTableHierarchy.Build(rows, levels);
Assert(groups.Count == 3, "Every unique order should be exactly one root group.");
Assert(groups[0].Key == "AB-1" && groups[1].Key == "AB-2",
    "Source order must remain intact.");
Assert(groups[0].Children.Count == 2, "Positions 10 and 20 must be separate.");
Assert(groups[0].Children[0].Rows.Count == 2,
    "Parallel tasks from the same department must be preserved in details.");
Assert(DynTableHierarchy.MaxOpenCount(groups[0], "OffenAuftrag") == 3,
    "Already-aggregated order counts must not be summed over joined rows.");
Assert(DynTableHierarchy.MaxOpenCount(groups[1], "OffenAuftrag") == 0,
    "Fully completed orders must remain visible.");
Assert(DynTableHierarchy.LatestCompleted(groups[1], "LetztesAuftragDatum") == finished,
    "Show last completion for finished order.");
var summaries = DynTableHierarchy.Summaries(groups[0], levels[0]);
Assert(summaries.Count == 2, "Only open departments; duplicate departments collapsed.");
Assert(summaries[0].Label == "AV-Team" && summaries[1].Label == "QS-Team",
    "Department sort order must follow explicit sort fields.");
Assert(groups[1].Path != groups[2].Path,
    "Composite keys must not collide.");
Assert(DynTableHierarchy.Build([], levels).Count == 0,
    "An empty dataset has no phantom group.");
Assert(DynTableHierarchy.Build(rows, levels[..1]).Count == 3,
    "One-level hierarchies are supported.");
Console.WriteLine("PASS hierarchical order > position grouping, open counts, completion, summaries and ordering");
