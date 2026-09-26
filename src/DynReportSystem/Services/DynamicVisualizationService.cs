using System.Collections;
using System.Globalization;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

public sealed class DynamicVisualizationService
{
    public AutoVisualization? Build(QueryResult result, string mode = "Auto")
    {
        if (result.Rows.Count == 0 || result.Columns.Count < 2)
            return null;

        var profile = Profile(result);
        if (profile.Numeric.Count == 0)
            return null;

        var requested = mode.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? ChooseAutoMode(result, profile)
            : mode;

        return requested.ToLowerInvariant() switch
        {
            "line" => BuildCartesian(result, profile, "line"),
            "area" => BuildCartesian(result, profile, "line", area: true),
            "bar" => BuildCartesian(result, profile, "bar"),
            "pie" => BuildPie(result, profile),
            _ => null
        };
    }

    public IReadOnlyList<string> Modes(QueryResult result)
    {
        var profile = Profile(result);
        var modes = new List<string> { "Auto", "Tabelle" };

        if (profile.Numeric.Count > 0)
        {
            modes.Add("Balken");
            modes.Add("Linie");
            modes.Add("Fläche");

            if (profile.Category is not null && profile.Numeric.Count == 1 && result.Rows.Count <= 20)
                modes.Add("Donut");
        }

        return modes;
    }

    public string NormalizeMode(string label) => label switch
    {
        "Tabelle" => "Table",
        "Balken" => "Bar",
        "Linie" => "Line",
        "Fläche" => "Area",
        "Donut" => "Pie",
        _ => label
    };

    private static string ChooseAutoMode(QueryResult result, DataProfile profile)
    {
        if (profile.Date is not null)
            return "Line";

        if (profile.Category is not null && result.Rows.Count <= 30)
            return "Bar";

        return "Table";
    }

    private static AutoVisualization? BuildCartesian(
        QueryResult result,
        DataProfile profile,
        string type,
        bool area = false)
    {
        var category = profile.Date ?? profile.Category;
        if (category is null)
            return null;

        var rows = result.Rows.Take(120).ToArray();
        var labels = rows.Select(row => FormatCategory(QueryResult.Get(row, category))).ToArray();
        var horizontal = type == "bar" && profile.Date is null && labels.Length > 8;

        var series = profile.Numeric.Take(5).Select(column =>
            (object)new Dictionary<string, object?>
            {
                ["name"] = column,
                ["type"] = type,
                ["smooth"] = type == "line",
                ["symbolSize"] = 6,
                ["barMaxWidth"] = 30,
                ["areaStyle"] = area ? new Dictionary<string, object?> { ["opacity"] = 0.12 } : null,
                ["data"] = rows.Select(row => Numeric(QueryResult.Get(row, column))).ToArray()
            }).ToArray();

        var categoryAxis = new Dictionary<string, object?>
        {
            ["type"] = "category",
            ["data"] = labels,
            ["axisTick"] = new Dictionary<string, object?> { ["show"] = false },
            ["axisLabel"] = new Dictionary<string, object?>
            {
                ["hideOverlap"] = true,
                ["overflow"] = "truncate",
                ["width"] = horizontal ? 170 : null
            }
        };

        var valueAxis = new Dictionary<string, object?>
        {
            ["type"] = "value",
            ["splitLine"] = new Dictionary<string, object?>
            {
                ["lineStyle"] = new Dictionary<string, object?> { ["color"] = "#edf2f3" }
            }
        };

        var option = new Dictionary<string, object?>
        {
            ["animationDuration"] = 450,
            ["__dynResponsive"] = horizontal ? "horizontal-bar" : "cartesian",
            ["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "axis" },
            ["legend"] = new Dictionary<string, object?>
            {
                ["type"] = "scroll",
                ["top"] = 0,
                ["right"] = 0
            },
            ["grid"] = new Dictionary<string, object?>
            {
                ["left"] = horizontal ? 150 : 55,
                ["right"] = 28,
                ["top"] = 45,
                ["bottom"] = 48,
                ["containLabel"] = true
            },
            ["xAxis"] = horizontal ? valueAxis : categoryAxis,
            ["yAxis"] = horizontal ? categoryAxis : valueAxis,
            ["toolbox"] = Toolbox(),
            ["series"] = series
        };

        if (!horizontal && labels.Length > 20)
        {
            option["dataZoom"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "inside", ["start"] = 0, ["end"] = 60 },
                new Dictionary<string, object?> { ["type"] = "slider", ["height"] = 14, ["bottom"] = 3 }
            };
        }

        return new AutoVisualization(
            area ? "Area" : type.Equals("bar", StringComparison.OrdinalIgnoreCase) ? "Bar" : "Line",
            $"{string.Join(" · ", profile.Numeric.Take(3))} nach {category}",
            option);
    }

    private static AutoVisualization? BuildPie(QueryResult result, DataProfile profile)
    {
        if (profile.Category is null || profile.Numeric.Count != 1)
            return null;

        var valueColumn = profile.Numeric[0];
        var data = result.Rows
            .Take(20)
            .Select(row => (object)new Dictionary<string, object?>
            {
                ["name"] = FormatCategory(QueryResult.Get(row, profile.Category)),
                ["value"] = Numeric(QueryResult.Get(row, valueColumn))
            })
            .ToArray();

        return new AutoVisualization(
            "Pie",
            $"{valueColumn} nach {profile.Category}",
            new Dictionary<string, object?>
            {
                ["__dynResponsive"] = "donut",
                ["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "item" },
                ["legend"] = new Dictionary<string, object?> { ["type"] = "scroll", ["bottom"] = 0 },
                ["toolbox"] = Toolbox(),
                ["series"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["name"] = valueColumn,
                        ["type"] = "pie",
                        ["radius"] = new[] { "45%", "70%" },
                        ["center"] = new[] { "50%", "44%" },
                        ["itemStyle"] = new Dictionary<string, object?>
                        {
                            ["borderColor"] = "#fff",
                            ["borderWidth"] = 3,
                            ["borderRadius"] = 5
                        },
                        ["label"] = new Dictionary<string, object?> { ["show"] = false },
                        ["data"] = data
                    }
                }
            });
    }

    private static DataProfile Profile(QueryResult result)
    {
        string? date = null;
        string? category = null;
        var numeric = new List<string>();

        foreach (var column in result.Columns)
        {
            var sample = result.Rows
                .Select(row => QueryResult.Get(row, column))
                .FirstOrDefault(value => value is not null);

            if (sample is null)
                continue;

            if (IsDate(sample))
            {
                date ??= column;
                continue;
            }

            if (IsNumeric(sample))
            {
                numeric.Add(column);
                continue;
            }

            if (sample is string)
                category ??= column;
        }

        return new DataProfile(date, category, numeric);
    }

    private static bool IsDate(object value) =>
        value is DateTime or DateTimeOffset or DateOnly;

    private static bool IsNumeric(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal;

    private static double? Numeric(object? value)
    {
        if (value is null)
            return null;

        try
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatCategory(object? value) => value switch
    {
        DateTime date => date.ToString("dd.MM.yy", CultureInfo.GetCultureInfo("de-AT")),
        DateTimeOffset dto => dto.ToString("dd.MM.yy", CultureInfo.GetCultureInfo("de-AT")),
        _ => Convert.ToString(value, CultureInfo.GetCultureInfo("de-AT")) ?? ""
    };

    private static object Toolbox() => new Dictionary<string, object?>
    {
        ["right"] = 0,
        ["top"] = 0,
        ["feature"] = new Dictionary<string, object?>
        {
            ["saveAsImage"] = new Dictionary<string, object?> { ["title"] = "Grafik speichern", ["pixelRatio"] = 2 }
        }
    };

    private sealed record DataProfile(string? Date, string? Category, List<string> Numeric);
}
