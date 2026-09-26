using System.Globalization;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

public sealed class RdlPresentationRenderer
{
    private static readonly CultureInfo DeAt = CultureInfo.GetCultureInfo("de-AT");

    public RdlRenderedVisual? Render(
        RdlPresentationItem item,
        QueryResult result,
        IReadOnlyDictionary<string, string> reportColors)
    {
        if (result.Rows.Count == 0)
            return null;

        return item.Kind switch
        {
            "Chart" when item.Chart is not null => RenderChart(item, item.Chart, result, reportColors),
            "Gauge" when item.Gauge is not null => RenderGauge(item, item.Gauge, result),
            _ => null
        };
    }

    private static RdlRenderedVisual? RenderChart(
        RdlPresentationItem item,
        RdlChartPresentation chart,
        QueryResult result,
        IReadOnlyDictionary<string, string> reportColors)
    {
        if (chart.Series.Count == 0)
            return null;

        var primary = chart.Series[0];
        var shape = IsShape(primary.Type);
        var categoryField = ResolveColumn(result, chart.CategoryField);
        var seriesGroupField = ResolveColumn(result, chart.SeriesGroupField);

        if (shape)
            return RenderShape(item, chart, result, categoryField, seriesGroupField, reportColors);

        var categories = CategoryValues(result, categoryField);
        if (categories.Count == 0)
            categories = [new CategoryBucket("Gesamt", result.Rows)];

        var dynamicSeries = !string.IsNullOrWhiteSpace(seriesGroupField)
            ? BuildDynamicSeries(chart, primary, categories, seriesGroupField!, reportColors)
            : BuildStaticSeries(chart, categories, result, reportColors);

        if (dynamicSeries.Count == 0)
            return null;

        var horizontal = primary.Type.Equals("Bar", StringComparison.OrdinalIgnoreCase)
                         && !primary.Type.Equals("Column", StringComparison.OrdinalIgnoreCase);
        var stacked = chart.Series.Any(x =>
            x.Subtype?.Contains("Stacked", StringComparison.OrdinalIgnoreCase) == true)
            || chart.Subtype?.Contains("Stacked", StringComparison.OrdinalIgnoreCase) == true;
        var area = chart.Series.Any(x => x.Type.Equals("Area", StringComparison.OrdinalIgnoreCase));
        var line = area || chart.Series.All(x => x.Type.Equals("Line", StringComparison.OrdinalIgnoreCase));

        var categoryAxis = new Dictionary<string, object?>
        {
            ["type"] = "category",
            ["data"] = categories.Select(x => x.Label).ToArray(),
            ["axisTick"] = new Dictionary<string, object?> { ["show"] = false },
            ["axisLabel"] = new Dictionary<string, object?>
            {
                ["hideOverlap"] = true,
                ["overflow"] = "truncate",
                ["width"] = horizontal ? 190 : null
            }
        };

        var valueAxis = new Dictionary<string, object?>
        {
            ["type"] = "value",
            ["scale"] = false,
            ["splitLine"] = new Dictionary<string, object?>
            {
                ["lineStyle"] = new Dictionary<string, object?> { ["color"] = "#edf2f3" }
            }
        };

        var seriesOptions = dynamicSeries.Select(s =>
        {
            var type = line ? "line" : "bar";
            var option = new Dictionary<string, object?>
            {
                ["name"] = s.Name,
                ["type"] = type,
                ["data"] = s.Values.Cast<object?>().ToArray(),
                ["smooth"] = type == "line",
                ["symbolSize"] = 6,
                ["barMaxWidth"] = 34,
                ["emphasis"] = new Dictionary<string, object?> { ["focus"] = "series" }
            };

            if (stacked)
                option["stack"] = "rdl-stack";

            if (area)
                option["areaStyle"] = new Dictionary<string, object?> { ["opacity"] = stacked ? 0.72 : 0.16 };

            if (!string.IsNullOrWhiteSpace(s.Color))
            {
                option["itemStyle"] = new Dictionary<string, object?> { ["color"] = s.Color };
                option["lineStyle"] = new Dictionary<string, object?> { ["color"] = s.Color, ["width"] = 2.3 };
            }

            return (object)option;
        }).ToArray();

        var optionRoot = new Dictionary<string, object?>
        {
            ["animationDuration"] = 420,
            ["__dynResponsive"] = horizontal ? "horizontal-bar" : stacked ? "grouped-stack" : "cartesian",
            ["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "axis" },
            ["legend"] = new Dictionary<string, object?>
            {
                ["show"] = chart.ShowLegend,
                ["type"] = "scroll",
                ["top"] = 0,
                ["left"] = 0,
                ["right"] = 38
            },
            ["grid"] = new Dictionary<string, object?>
            {
                ["left"] = horizontal ? 135 : 55,
                ["right"] = 25,
                ["top"] = chart.ShowLegend ? 46 : 18,
                ["bottom"] = categories.Count > 14 ? 58 : 38,
                ["containLabel"] = true
            },
            ["xAxis"] = horizontal ? valueAxis : categoryAxis,
            ["yAxis"] = horizontal ? categoryAxis : valueAxis,
            ["series"] = seriesOptions,
            ["toolbox"] = Toolbox()
        };

        if (!horizontal && categories.Count > 18)
        {
            optionRoot["dataZoom"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "inside", ["start"] = 0, ["end"] = 70 },
                new Dictionary<string, object?> { ["type"] = "slider", ["height"] = 13, ["bottom"] = 2 }
            };
        }

        return new RdlRenderedVisual(
            "Chart",
            item.Title,
            optionRoot,
            Subtitle: stacked ? "Gestapelte Darstellung entsprechend der SSRS-Definition" : null);
    }

    private static RdlRenderedVisual? RenderShape(
        RdlPresentationItem item,
        RdlChartPresentation chart,
        QueryResult result,
        string? categoryField,
        string? seriesGroupField,
        IReadOnlyDictionary<string, string> reportColors)
    {
        var template = chart.Series[0];
        var valueField = ResolveColumn(result, template.ValueField);
        if (string.IsNullOrWhiteSpace(valueField))
            return null;

        string groupField = categoryField ?? seriesGroupField ?? "";
        List<(string Name, double Value, string? Color)> points;

        if (!string.IsNullOrWhiteSpace(groupField))
        {
            points = result.Rows
                .GroupBy(row => Display(QueryResult.Get(row, groupField)), StringComparer.CurrentCultureIgnoreCase)
                .Select(group =>
                {
                    var value = Aggregate(group, valueField, template.Aggregate);
                    var color = ColorFor(group.Key, template, chart, reportColors, group.Count());
                    return (group.Key, value, color);
                })
                .OrderByDescending(x => Math.Abs(x.Value))
                .Take(30)
                .ToList();
        }
        else
        {
            points = chart.Series
                .Select((series, index) =>
                {
                    var field = ResolveColumn(result, series.ValueField);
                    var value = field is null ? 0d : Aggregate(result.Rows, field, series.Aggregate);
                    return (series.Name, value, ColorFor(series.Name, series, chart, reportColors, index));
                })
                .ToList();
        }

        var data = points.Select(x =>
        {
            var point = new Dictionary<string, object?>
            {
                ["name"] = x.Name,
                ["value"] = x.Value
            };

            if (!string.IsNullOrWhiteSpace(x.Color))
                point["itemStyle"] = new Dictionary<string, object?> { ["color"] = x.Color };

            return (object)point;
        }).ToArray();

        var doughnut = chart.Series.Any(x =>
            x.Subtype?.Contains("Doughnut", StringComparison.OrdinalIgnoreCase) == true)
            || chart.Subtype?.Contains("Doughnut", StringComparison.OrdinalIgnoreCase) == true;

        var options = new Dictionary<string, object?>
        {
            ["animationDuration"] = 420,
            ["__dynResponsive"] = "donut",
            ["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "item" },
            ["legend"] = new Dictionary<string, object?>
            {
                ["show"] = chart.ShowLegend,
                ["type"] = "scroll",
                ["bottom"] = 0
            },
            ["toolbox"] = Toolbox(),
            ["series"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = item.Title,
                    ["type"] = "pie",
                    ["radius"] = doughnut ? new[] { "46%", "70%" } : new[] { "0%", "70%" },
                    ["center"] = new[] { "50%", "43%" },
                    ["itemStyle"] = new Dictionary<string, object?>
                    {
                        ["borderColor"] = "#fff",
                        ["borderWidth"] = 3,
                        ["borderRadius"] = 4
                    },
                    ["label"] = new Dictionary<string, object?>
                    {
                        ["show"] = points.Count <= 8,
                        ["formatter"] = "{b}\n{d}%"
                    },
                    ["data"] = data
                }
            }
        };

        return new RdlRenderedVisual("Chart", item.Title, options);
    }

    private static RdlRenderedVisual RenderGauge(
        RdlPresentationItem item,
        RdlGaugePresentation gauge,
        QueryResult result)
    {
        var field = ResolveColumn(result, gauge.ValueField);
        var value = field is null ? 0d : Aggregate(result.Rows, field, gauge.Aggregate);
        var minimum = gauge.Minimum ?? 0d;
        var maximum = gauge.Maximum ?? Math.Max(Math.Abs(value) * 1.2d, 1d);

        if (maximum <= minimum)
            maximum = minimum + Math.Max(Math.Abs(value), 1d);

        var ratio = Math.Clamp((value - minimum) / (maximum - minimum), 0d, 1d);
        var display = FormatNumber(value);

        object options;

        if (gauge.GaugeType.Equals("Radial", StringComparison.OrdinalIgnoreCase))
        {
            options = new Dictionary<string, object?>
            {
                ["animationDuration"] = 420,
                ["series"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "gauge",
                        ["min"] = minimum,
                        ["max"] = maximum,
                        ["startAngle"] = 210,
                        ["endAngle"] = -30,
                        ["progress"] = new Dictionary<string, object?> { ["show"] = true, ["width"] = 16 },
                        ["axisLine"] = new Dictionary<string, object?>
                        {
                            ["lineStyle"] = new Dictionary<string, object?> { ["width"] = 16 }
                        },
                        ["pointer"] = new Dictionary<string, object?> { ["show"] = false },
                        ["axisTick"] = new Dictionary<string, object?> { ["show"] = false },
                        ["splitLine"] = new Dictionary<string, object?> { ["show"] = false },
                        ["axisLabel"] = new Dictionary<string, object?> { ["show"] = false },
                        ["detail"] = new Dictionary<string, object?>
                        {
                            ["valueAnimation"] = true,
                            ["fontSize"] = 22,
                            ["formatter"] = display
                        },
                        ["data"] = new object[] { new Dictionary<string, object?> { ["value"] = value } }
                    }
                }
            };
        }
        else
        {
            options = new Dictionary<string, object?>
            {
                ["animationDuration"] = 420,
                ["grid"] = new Dictionary<string, object?> { ["left"] = 18, ["right"] = 18, ["top"] = 28, ["bottom"] = 20 },
                ["xAxis"] = new Dictionary<string, object?> { ["type"] = "value", ["min"] = minimum, ["max"] = maximum, ["show"] = false },
                ["yAxis"] = new Dictionary<string, object?> { ["type"] = "category", ["data"] = new[] { item.Title }, ["show"] = false },
                ["series"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "bar",
                        ["data"] = new[] { value },
                        ["barWidth"] = 22,
                        ["showBackground"] = true,
                        ["backgroundStyle"] = new Dictionary<string, object?> { ["borderRadius"] = 10 },
                        ["itemStyle"] = new Dictionary<string, object?> { ["borderRadius"] = 10 },
                        ["label"] = new Dictionary<string, object?> { ["show"] = true, ["position"] = "right", ["formatter"] = display }
                    }
                }
            };
        }

        return new RdlRenderedVisual(
            "Gauge",
            item.Title,
            options,
            display,
            $"{ratio:P0} des definierten Bereichs");
    }

    private static List<RenderedSeries> BuildDynamicSeries(
        RdlChartPresentation chart,
        RdlChartSeriesPresentation template,
        IReadOnlyList<CategoryBucket> categories,
        string seriesGroupField,
        IReadOnlyDictionary<string, string> reportColors)
    {
        var valueField = categories
            .SelectMany(x => x.Rows)
            .SelectMany(row => row.Keys)
            .FirstOrDefault(column =>
                !string.IsNullOrWhiteSpace(template.ValueField)
                && column.Equals(template.ValueField, StringComparison.OrdinalIgnoreCase));

        if (valueField is null)
            return [];

        var groupNames = categories
            .SelectMany(x => x.Rows)
            .Select(row => Display(QueryResult.Get(row, seriesGroupField)))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var result = new List<RenderedSeries>();
        for (var index = 0; index < groupNames.Length; index++)
        {
            var groupName = groupNames[index];
            var values = categories.Select(category =>
                Aggregate(
                    category.Rows.Where(row =>
                        Display(QueryResult.Get(row, seriesGroupField))
                            .Equals(groupName, StringComparison.CurrentCultureIgnoreCase)),
                    valueField,
                    template.Aggregate)).ToArray();

            result.Add(new RenderedSeries(
                groupName,
                values,
                ColorFor(groupName, template, chart, reportColors, index)));
        }

        return result;
    }

    private static List<RenderedSeries> BuildStaticSeries(
        RdlChartPresentation chart,
        IReadOnlyList<CategoryBucket> categories,
        QueryResult result,
        IReadOnlyDictionary<string, string> reportColors)
    {
        var rendered = new List<RenderedSeries>();

        for (var index = 0; index < chart.Series.Count; index++)
        {
            var series = chart.Series[index];
            var field = ResolveColumn(result, series.ValueField);
            if (field is null)
                continue;

            var values = categories
                .Select(category => Aggregate(category.Rows, field, series.Aggregate))
                .ToArray();

            rendered.Add(new RenderedSeries(
                series.Name,
                values,
                ColorFor(series.Name, series, chart, reportColors, index)));
        }

        return rendered;
    }

    private static List<CategoryBucket> CategoryValues(QueryResult result, string? categoryField)
    {
        if (string.IsNullOrWhiteSpace(categoryField))
            return [];

        return result.Rows
            .GroupBy(row => Display(QueryResult.Get(row, categoryField)), StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new CategoryBucket(group.Key, group.ToList()))
            .ToList();
    }

    private static string? ResolveColumn(QueryResult result, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return null;

        return result.Columns.FirstOrDefault(x =>
            x.Equals(requested, StringComparison.OrdinalIgnoreCase));
    }

    private static double Aggregate(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        string field,
        string aggregate)
    {
        var values = rows
            .Select(row => Number(QueryResult.Get(row, field)))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToArray();

        if (aggregate.Equals("Count", StringComparison.OrdinalIgnoreCase))
            return values.Length;

        if (values.Length == 0)
            return 0;

        return aggregate.ToLowerInvariant() switch
        {
            "avg" or "average" => values.Average(),
            "min" => values.Min(),
            "max" => values.Max(),
            "first" => values.First(),
            "last" => values.Last(),
            _ => values.Sum()
        };
    }

    private static string? ColorFor(
        string key,
        RdlChartSeriesPresentation series,
        RdlChartPresentation chart,
        IReadOnlyDictionary<string, string> reportColors,
        int index)
    {
        if (!string.IsNullOrWhiteSpace(series.Color))
            return series.Color;

        if (reportColors.TryGetValue(key, out var mapped))
            return mapped;

        if (chart.CustomPalette.Count > 0)
            return chart.CustomPalette[index % chart.CustomPalette.Count];

        return null;
    }

    private static bool IsShape(string type) =>
        type.Equals("Shape", StringComparison.OrdinalIgnoreCase);

    private static double? Number(object? value)
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

    private static string Display(object? value) => value switch
    {
        null => "",
        DateTime date => date.ToString("dd.MM.yy", DeAt),
        DateTimeOffset dto => dto.ToString("dd.MM.yy", DeAt),
        _ => Convert.ToString(value, DeAt)?.Trim() ?? ""
    };

    private static string FormatNumber(double value)
    {
        if (Math.Abs(value) >= 1_000_000)
            return $"{value / 1_000_000d:N1} Mio.";
        if (Math.Abs(value) >= 1_000)
            return $"{value / 1_000d:N1} Tsd.";
        return value.ToString("N1", DeAt);
    }

    private static object Toolbox() => new Dictionary<string, object?>
    {
        ["right"] = 0,
        ["top"] = 0,
        ["feature"] = new Dictionary<string, object?>
        {
            ["saveAsImage"] = new Dictionary<string, object?> { ["title"] = "Grafik speichern", ["pixelRatio"] = 2 }
        }
    };

    private sealed record CategoryBucket(
        string Label,
        IReadOnlyList<Dictionary<string, object?>> Rows);

    private sealed record RenderedSeries(
        string Name,
        IReadOnlyList<double> Values,
        string? Color);
}
