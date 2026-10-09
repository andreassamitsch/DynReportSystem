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
