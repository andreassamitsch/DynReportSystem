using System.Text.Json;
using DynReportSystem.Models;
using DynReportSystem.Services;
using Microsoft.Extensions.Configuration;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("Stack total regression: " + message);
}

static JsonElement FindOptions(JsonElement value)
{
    if (value.ValueKind == JsonValueKind.Object)
    {
        if (value.TryGetProperty("__dynStackTotalSeries", out _))
            return value;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var candidate = FindOptions(property.Value);
                if (candidate.ValueKind == JsonValueKind.Object) return candidate;
            }
        }
    }
    if (value.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var candidate = FindOptions(item);
                if (candidate.ValueKind == JsonValueKind.Object) return candidate;
            }
        }
    }
    return default;
}

static DynChartDefinition Definition(bool horizontal = false) => new()
{
    ChartType = "Bar",
    CategoryField = "Monat",
    Orientation = horizontal ? "Horizontal" : "Vertical",
    Stacked = true,
    ShowStackTotal = true,
    ShowLabels = false,
    StackTotalFormat = "currency",
    Series =
    [
        new() { Field = "Umsatz", Label = "Umsatz", ChartType = "Bar", Format = "currency" },
        new() { Field = "OffenerAuftragswert", Label = "Offener Auftragsbestand", ChartType = "Bar", Format = "currency" },
        new() { Field = "Rahmenwert", Label = "Rahmenplanung", ChartType = "Bar", Format = "currency" }
    ]
};

var rows = new[]
{
    new Dictionary<string, object?>
    {
        ["Monat"] = "01.10.26", ["Umsatz"] = 337110d,
        ["OffenerAuftragswert"] = 1260490d, ["Rahmenwert"] = 126808d
    },
    new Dictionary<string, object?>
    {
        ["Monat"] = "01.11.26", ["Umsatz"] = 100000d,
        ["OffenerAuftragswert"] = 200000d, ["Rahmenwert"] = 0d
    }
};
var result = new QueryResult
{
    Dataset = "DashboardMonat",
    Columns = ["Monat", "Umsatz", "OffenerAuftragswert", "Rahmenwert"],
    Rows = rows
};
var service = new DynamicVisualizationService(
    new ReportVisualThemeService(new ConfigurationBuilder().Build()));

foreach (var horizontal in new[] { false, true })
{
    var visual = service.Build(result, Definition(horizontal));
    Require(visual is not null, "native chart should exist");
    using var document = JsonDocument.Parse(JsonSerializer.Serialize(visual));
    var option = FindOptions(document.RootElement);
    Require(option.ValueKind == JsonValueKind.Object, "total-label option missing");
    Require(option.GetProperty("__dynStackTotalSeries").GetString() == "__dyn_stack_total__",
        "the label series key changed");
    Require(option.GetProperty("__dynStackTotalFormat").GetString() == "currency",
        "currency format not preserved");
    Require(option.GetProperty("__dynResponsive").GetString() ==
        (horizontal ? "horizontal-stack" : "primary-stack"),
        "total helper must not change the responsive stacked-bar classification");

    var helper = option.GetProperty("series").EnumerateArray()
        .Single(item => item.GetProperty("name").GetString() == "__dyn_stack_total__");
    Require(helper.GetProperty("symbol").GetString() == "circle",
        "invisible symbol suppresses line labels in ECharts");
    Require(helper.GetProperty("symbolSize").GetInt32() > 0,
        "line label must have a real marker anchor");
    Require(helper.GetProperty("showSymbol").GetBoolean(),
        "the helper marker must be present for data labels");
    Require(!helper.GetProperty("clip").GetBoolean(),
        "stack-total label must not be clipped at the plot boundary");
    Require(helper.GetProperty("itemStyle").GetProperty("color").GetString() ==
        "rgba(0,0,0,0)", "marker must remain invisible without hiding its label");
    Require(!helper.GetProperty("label").GetProperty("show").GetBoolean(),
        "renderer JS still owns display format and collision handling");
    var data = helper.GetProperty("data").EnumerateArray().ToArray();
    Require(data.Length == 2, "expected two monthly totals");
    var first = data[0].GetProperty("stackTotal").GetDouble();
    Require(Math.Abs(first - (337110d + 1260490d + 126808d)) < 0.01,
        "the three stacked values must sum correctly");
}
Console.WriteLine("PASS stacked totals: positive label anchors, hidden markers, currency, correct sums and layout");


// Production cockpit regression: test only the known rendering and expression
// invariants. Exact SSRS equivalence requires the source RDL and same-time data.
var heatmap = ProductionMetricHeatmap.Style(8.7m, 6m, "LowerIsBetter");
Require(heatmap.Contains("background-color:rgb(", StringComparison.Ordinal),
    "overdue setup must receive a continuous severity shade");
Require(ProductionMetricHeatmap.Style(4m, 6m, "LowerIsBetter").Contains("#c8efcf"),
    "better-than-target setup must stay green");
Require(ProductionMetricHeatmap.Style(7m, 10m, "HigherIsBetter").Contains("background-color:rgb("),
    "quantity below expected target is adverse");
Require(ProductionMetricHeatmap.Style(12m, 10m, "HigherIsBetter").Contains("#c8efcf"),
    "quantity ahead of plan is green");
Require(ProductionMetricHeatmap.Style(null, 10m, "LowerIsBetter") == "",
    "null actual value must not be color-coded");
Require(ProductionMetricHeatmap.Style(346m, 0m, "LowerIsBetter") == "",
    "zero targets must not be color-coded");
Require(ProductionMetricHeatmap.AdverseDeviation(14m, 5m, "LowerIsBetter")
    > ProductionMetricHeatmap.AdverseDeviation(6m, 5m, "LowerIsBetter"),
    "more severe overruns must receive stronger shading");
Require(ProductionMetricHeatmap.AdverseDeviation(0m, 10m, "HigherIsBetter")
    > ProductionMetricHeatmap.AdverseDeviation(8m, 10m, "HigherIsBetter"),
    "larger production shortages must receive stronger shading");

var productionEngine = new DynExpressionEngine();
var duplicatedStatusRows = new List<Dictionary<string, object?>>
{
    new() { ["Period"] = "P1", ["Minutes"] = 100m },
    new() { ["Period"] = "P1", ["Minutes"] = 100m },
    new() { ["Period"] = "P2", ["Minutes"] = 24m },
};
var unduplicatedDuration = productionEngine.Evaluate(
    new DynExpr
    {
        Op = "sumDistinct",
        Field = "Minutes",
        DistinctBy = ["Period"]
    },
    new DynReportDocument(),
    duplicatedStatusRows);
Require(Convert.ToDecimal(unduplicatedDuration) == 124m,
    "status duration must not be counted twice across duplicate rows");

// KNOWN LEGACY CAVEAT: when no released parts are recorded, the existing
// divide-by-one fallback turns 346 production minutes into '346 min/part'.
// No SSRS business expression is changed by the 0.19.4 layout update.
var minutesWithNoReleases = productionEngine.Evaluate(
    new DynExpr
    {
        Op = "divide",
        Args =
        [
            new DynExpr
            {
                Op = "const",
                Value = JsonSerializer.SerializeToElement(346m)
            },
            new DynExpr
            {
                Op = "maxOf",
                Args =
                [
                    new DynExpr
                    {
                        Op = "const",
                        Value = JsonSerializer.SerializeToElement(0m)
                    },
                    new DynExpr
                    {
                        Op = "const",
                        Value = JsonSerializer.SerializeToElement(1m)
                    }
                ]
            }
        ]
    },
    new DynReportDocument(),
    duplicatedStatusRows);
Require(Convert.ToDecimal(minutesWithNoReleases) == 346m,
    "known zero-release fallback must be documented before changing any business logic");

Console.WriteLine("PASS production heatmap: direction, continuous severity, missing target, distinct status duration and zero-release fallback");
