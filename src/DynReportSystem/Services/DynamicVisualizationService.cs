using System.Collections;
using System.Globalization;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

public sealed class DynamicVisualizationService(ReportVisualThemeService themes)
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

    public AutoVisualization? Build(QueryResult result, DynChartDefinition? definition)
    {
        if (definition is null)
            return Build(result, "Auto");

        var chartType = string.IsNullOrWhiteSpace(definition.ChartType)
            ? "Auto"
            : definition.ChartType;

        var configuredSeries = definition.Series
            .Where(x => !string.IsNullOrWhiteSpace(x.Field)
                && result.Columns.Contains(x.Field, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (configuredSeries.Length == 0 || string.IsNullOrWhiteSpace(definition.CategoryField)
            || !result.Columns.Contains(definition.CategoryField, StringComparer.OrdinalIgnoreCase))
        {
            return Build(result, chartType);
        }

        return BuildConfigured(result, definition, configuredSeries);
    }

    private AutoVisualization? BuildConfigured(
        QueryResult result,
        DynChartDefinition definition,
        IReadOnlyList<DynChartSeries> configuredSeries)
    {
        var type = definition.ChartType.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? "bar"
            : definition.ChartType.ToLowerInvariant();

        if (type == "donut")
            type = "pie";

        if (type is "worldmap" or "world-map" or "map")
            return BuildWorldMap(result, definition, configuredSeries[0]);

        if (type == "treemap")
            return BuildTreemap(result, definition, configuredSeries[0]);

        var categoryField = definition.CategoryField;
        var seriesBy = !string.IsNullOrWhiteSpace(definition.SeriesByField)
            && result.Columns.Contains(definition.SeriesByField, StringComparer.OrdinalIgnoreCase)
                ? definition.SeriesByField
                : "";

        var maxPoints = Math.Clamp(definition.MaxPoints, 1, 5000);
        var categoryCandidates = result.Rows
            .Select((row, index) => new
            {
                Label = FormatCategory(QueryResult.Get(row, categoryField)),
                Raw = QueryResult.Get(row, categoryField),
                Index = index
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Label))
            .GroupBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var categorySort = definition.Sort.FirstOrDefault();
        if (categorySort is not null
            && categorySort.Field.Equals(categoryField, StringComparison.OrdinalIgnoreCase))
        {
            categoryCandidates = categorySort.Direction.Equals("Desc", StringComparison.OrdinalIgnoreCase)
                ? categoryCandidates.OrderByDescending(x => x.Raw, ChartCategoryComparer.Instance).ToList()
                : categoryCandidates.OrderBy(x => x.Raw, ChartCategoryComparer.Instance).ToList();
        }
        else if (categoryCandidates.Count > 0
                 && categoryCandidates.All(x => IsDate(x.Raw)))
        {
            categoryCandidates = categoryCandidates
                .OrderBy(x => x.Raw, ChartCategoryComparer.Instance)
                .ToList();
        }
        else
        {
            categoryCandidates = categoryCandidates
                .OrderBy(x => x.Index)
                .ToList();
        }

        var categories = categoryCandidates
            .Take(maxPoints)
            .Select(x => x.Label)
            .ToArray();

        if (categories.Length == 0)
            return null;

        if (type == "pie")
        {
            var measure = configuredSeries[0];
            var data = categories.Select(category =>
            {
                var groupedRows = result.Rows.Where(row =>
                    string.Equals(
                        FormatCategory(QueryResult.Get(row, categoryField)),
                        category,
                        StringComparison.CurrentCultureIgnoreCase))
                    .ToArray();

                var item = new Dictionary<string, object?>
                {
                    ["name"] = category,
                    ["value"] = Aggregate(groupedRows, measure.Field, measure.Aggregation)
                };

                if (TryChartColor(definition, groupedRows, category, out var color))
                {
                    item["itemStyle"] = new Dictionary<string, object?>
                    {
                        ["color"] = color
                    };
                }

                return (object)item;
            }).ToArray();

            var pieSeries = new Dictionary<string, object?>
            {
                ["name"] = SeriesLabel(measure),
                ["type"] = "pie",
                ["radius"] = new[] { "45%", "70%" },
                ["center"] = new[] { "50%", "44%" },
                ["label"] = new Dictionary<string, object?>
                {
                    ["show"] = definition.ShowLabels,
                    ["formatter"] = "{b}"
                },
                ["data"] = data
            };

            if (IsHexColor(measure.Color))
                pieSeries["color"] = new[] { measure.Color };

            return new AutoVisualization(
                "Pie",
                $"{SeriesLabel(measure)} nach {categoryField}",
                new Dictionary<string, object?>
                {
                    ["__dynItemFormat"] = measure.Format,
                    ["__dynResponsive"] = "donut",
                    ["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "item" },
                    ["legend"] = new Dictionary<string, object?>
                    {
                        ["show"] = definition.ShowLegend,
                        ["type"] = "scroll",
                        ["bottom"] = 0
                    },
                    ["toolbox"] = Toolbox(),
                    ["series"] = new object[] { pieSeries }
                });
        }

        var groupValues = string.IsNullOrWhiteSpace(seriesBy)
            ? new[] { "" }
            : result.Rows
                .Select(row => FormatCategory(QueryResult.Get(row, seriesBy)))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Take(50)
                .ToArray();

        var series = new List<Dictionary<string, object?>>();
        var formats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var formatBySeries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var measure in configuredSeries)
        {
            foreach (var group in groupValues)
            {
                var label = string.IsNullOrWhiteSpace(group)
                    ? SeriesLabel(measure)
                    : configuredSeries.Count == 1
                        ? group
                        : $"{SeriesLabel(measure)} · {group}";

                formats[label] = measure.Format;
                formatBySeries[label] = measure.Format;

                var values = categories.Select(category =>
                {
                    var rows = result.Rows.Where(row =>
                        string.Equals(
                            FormatCategory(QueryResult.Get(row, categoryField)),
                            category,
                            StringComparison.CurrentCultureIgnoreCase)
                        && (string.IsNullOrWhiteSpace(group)
                            || string.Equals(
                                FormatCategory(QueryResult.Get(row, seriesBy)),
                                group,
                                StringComparison.CurrentCultureIgnoreCase)));

                    return Aggregate(rows, measure.Field, measure.Aggregation);
                }).ToArray();

                object chartData = values;

                if (string.IsNullOrWhiteSpace(seriesBy)
                    && (!string.IsNullOrWhiteSpace(definition.ColorSet)
                        || definition.ColorOverrides.Count > 0))
                {
                    chartData = categories.Select((category, index) =>
                    {
                        var categoryRows = result.Rows
                            .Where(row => string.Equals(
                                FormatCategory(QueryResult.Get(row, categoryField)),
                                category,
                                StringComparison.CurrentCultureIgnoreCase))
                            .ToArray();

                        var point = new Dictionary<string, object?>
                        {
                            ["value"] = values[index],
                            ["categoryLabel"] = category
                        };

                        if (TryChartColor(definition, categoryRows, category, out var categoryColor))
                        {
                            point["itemStyle"] = new Dictionary<string, object?>
                            {
                                ["color"] = categoryColor
                            };
                        }

                        return (object)point;
                    }).ToArray();
                }

                var effectiveType = string.IsNullOrWhiteSpace(measure.ChartType)
                    ? type
                    : measure.ChartType.Trim().ToLowerInvariant();

                if (effectiveType == "area")
                    effectiveType = "line";

                var isLine = effectiveType == "line";
                var isBar = effectiveType == "bar";
                var useArea = measure.Area
                    || measure.ChartType.Equals("Area", StringComparison.OrdinalIgnoreCase)
                    || type == "area";

                var entry = new Dictionary<string, object?>
                {
                    ["name"] = label,
                    ["type"] = effectiveType,
                    ["smooth"] = isLine,
                    ["symbolSize"] = isLine ? 6 : null,
                    ["barMaxWidth"] = isBar ? 34 : null,
                    ["stack"] = definition.Stacked && isBar ? "dyn-total" : null,
                    ["areaStyle"] = useArea
                        ? new Dictionary<string, object?> { ["opacity"] = 0.14 }
                        : null,
                    ["label"] = new Dictionary<string, object?>
                    {
                        ["show"] = definition.ShowLabels,
                        ["position"] = definition.Orientation.Equals("Horizontal", StringComparison.OrdinalIgnoreCase)
                            ? "right"
                            : "top"
                    },
                    ["data"] = chartData
                };

                if (measure.Axis > 0)
                    entry["yAxisIndex"] = measure.Axis;

                var groupRows = string.IsNullOrWhiteSpace(group)
                    ? result.Rows
                    : result.Rows
                        .Where(row => string.Equals(
                            FormatCategory(QueryResult.Get(row, seriesBy)),
                            group,
                            StringComparison.CurrentCultureIgnoreCase))
                        .ToArray();

                var explicitColor = IsHexColor(measure.Color)
                    ? measure.Color
                    : null;

                if (explicitColor is not null
                    || TryChartColor(definition, groupRows, group, out explicitColor))
                {
                    entry["itemStyle"] = new Dictionary<string, object?> { ["color"] = explicitColor };
                    entry["lineStyle"] = new Dictionary<string, object?>
                    {
                        ["color"] = explicitColor,
                        ["width"] = 2.5
                    };
                }

                series.Add(entry);
            }
        }

        if (definition.Stacked && definition.ShowStackTotal)
        {
            var stackedBars = series
                .Where(entry =>
                    string.Equals(Convert.ToString(entry.GetValueOrDefault("type")), "bar", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Convert.ToString(entry.GetValueOrDefault("stack")), "dyn-total", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (stackedBars.Length > 0)
            {
                var totals = Enumerable.Range(0, categories.Length)
                    .Select(index => stackedBars.Sum(entry =>
                    {
                        if (entry["data"] is not Array array || index >= array.Length)
                            return 0d;

                        return Numeric(array.GetValue(index)) ?? 0d;
                    }))
                    .ToArray();

                var labelSeries = stackedBars[^1];
                var raw = labelSeries["data"] as Array;
                if (raw is not null)
                {
                    labelSeries["data"] = Enumerable.Range(0, raw.Length)
                        .Select(index => (object)new Dictionary<string, object?>
                        {
                            ["value"] = raw.GetValue(index),
                            ["stackTotal"] = index < totals.Length ? totals[index] : 0d
                        })
                        .ToArray();

                    var name = Convert.ToString(labelSeries.GetValueOrDefault("name")) ?? "";
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        labelSeries["label"] = new Dictionary<string, object?>
                        {
                            ["show"] = false
                        };
                    }
                }
            }
        }

        var horizontal = definition.Orientation.Equals("Horizontal", StringComparison.OrdinalIgnoreCase)
            && series.All(entry =>
                string.Equals(Convert.ToString(entry.GetValueOrDefault("type")), "bar", StringComparison.OrdinalIgnoreCase));

        var categoryAxis = new Dictionary<string, object?>
        {
            ["type"] = "category",
            ["data"] = categories,
            ["axisTick"] = new Dictionary<string, object?> { ["show"] = false },
            ["axisLabel"] = new Dictionary<string, object?>
            {
                ["hideOverlap"] = true,
                ["overflow"] = "truncate",
                ["width"] = horizontal ? 160 : null
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

        var hasSecondAxis = series.Any(entry =>
            Convert.ToInt32(entry.GetValueOrDefault("yAxisIndex") ?? 0, CultureInfo.InvariantCulture) > 0);

        object yAxis = horizontal
            ? categoryAxis
            : hasSecondAxis
                ? new object[]
                {
                    valueAxis,
                    new Dictionary<string, object?>
                    {
                        ["type"] = "value",
                        ["splitLine"] = new Dictionary<string, object?> { ["show"] = false }
                    }
                }
                : valueAxis;

        var option = new Dictionary<string, object?>
        {
            ["__dynSeriesFormats"] = formats,
            ["__dynResponsive"] = horizontal
                ? definition.Stacked ? "horizontal-stack" : "horizontal-bar"
                : definition.Stacked ? "primary-stack" : "cartesian",
            ["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "axis" },
            ["legend"] = new Dictionary<string, object?>
            {
                ["show"] = definition.ShowLegend,
                ["type"] = "scroll",
                ["top"] = 0,
                ["left"] = 0,
                ["right"] = 34
            },
            ["grid"] = new Dictionary<string, object?>
            {
                ["left"] = horizontal ? 8 : 48,
                ["right"] = horizontal ? 70 : hasSecondAxis ? 52 : 20,
                ["top"] = definition.ShowLegend ? 34 : 10,
                ["bottom"] = !horizontal && categories.Length > 20 ? 56 : 32,
                ["containLabel"] = true
            },
            ["xAxis"] = horizontal ? valueAxis : categoryAxis,
            ["yAxis"] = yAxis,
            ["toolbox"] = Toolbox(),
            ["series"] = series.Cast<object>().ToArray()
        };

        if (definition.Stacked && definition.ShowStackTotal)
        {
            var stackedBars = series.Where(entry =>
                string.Equals(Convert.ToString(entry.GetValueOrDefault("type")), "bar", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Convert.ToString(entry.GetValueOrDefault("stack")), "dyn-total", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (stackedBars.Length > 0)
            {
                var labelSeriesName = Convert.ToString(stackedBars[^1].GetValueOrDefault("name")) ?? "";
                if (!string.IsNullOrWhiteSpace(labelSeriesName))
                {
                    option["__dynStackTotalSeries"] = labelSeriesName;
                    option["__dynStackTotalFormat"] = string.IsNullOrWhiteSpace(definition.StackTotalFormat)
                        ? formatBySeries.GetValueOrDefault(labelSeriesName) ?? "number"
                        : definition.StackTotalFormat;
                }
            }
        }

        if (!horizontal && categories.Length > 20)
        {
            option["dataZoom"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "inside", ["start"] = 0, ["end"] = 60 },
                new Dictionary<string, object?> { ["type"] = "slider", ["height"] = 12, ["bottom"] = 2 }
            };
        }

        return new AutoVisualization(
            definition.ChartType,
            $"{string.Join(" · ", configuredSeries.Select(SeriesLabel))} nach {categoryField}",
            option);
    }

    private AutoVisualization? BuildWorldMap(
        QueryResult result,
        DynChartDefinition definition,
        DynChartSeries measure)
    {
        var categoryField = definition.CategoryField;
        var groups = result.Rows
            .GroupBy(
                row => FormatCategory(QueryResult.Get(row, categoryField)).Trim().ToUpperInvariant(),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Take(Math.Clamp(definition.MaxPoints, 1, 5000))
            .ToArray();

        if (groups.Length == 0)
            return null;

        var tooltipFields = definition.TooltipFields
            .Where(field => result.Columns.Contains(field, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var data = groups.Select(group =>
        {
            var rows = group.ToArray();
            var item = new Dictionary<string, object?>
            {
                ["name"] = group.Key,
                ["value"] = Aggregate(rows, measure.Field, measure.Aggregation)
            };

            if (tooltipFields.Length > 0)
            {
                var top = rows
                    .OrderByDescending(row =>
                        Math.Abs(Numeric(QueryResult.Get(row, measure.Field)) ?? 0d))
                    .FirstOrDefault();

                if (top is not null)
                {
                    item["meta"] = tooltipFields.ToDictionary(
                        field => field,
                        field => QueryResult.Get(top, field));
                }
            }

            return (object)item;
        }).ToArray();

        var numeric = data
            .OfType<Dictionary<string, object?>>()
            .Select(item => Numeric(item.GetValueOrDefault("value")) ?? 0d)
            .Select(Math.Abs)
            .DefaultIfEmpty(1d)
            .Max();

        return new AutoVisualization(
            "WorldMap",
            $"{SeriesLabel(measure)} nach {categoryField}",
            new Dictionary<string, object?>
            {
                ["__dynMap"] = "world",
                ["__dynItemFormat"] = measure.Format,
                ["__dynTooltipFields"] = tooltipFields,
                ["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "item" },
                ["visualMap"] = new Dictionary<string, object?>
                {
                    ["min"] = 0,
                    ["max"] = numeric <= 0 ? 1 : numeric,
                    ["left"] = 10,
                    ["bottom"] = 10,
                    ["calculable"] = true,
                    ["textStyle"] = new Dictionary<string, object?> { ["color"] = "#61767e" }
                },
                ["toolbox"] = Toolbox(),
                ["series"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["name"] = SeriesLabel(measure),
                        ["type"] = "map",
                        ["map"] = "dyn-world",
                        ["roam"] = true,
                        ["selectedMode"] = false,
                        ["label"] = new Dictionary<string, object?> { ["show"] = definition.ShowLabels },
                        ["data"] = data,
                        ["emphasis"] = new Dictionary<string, object?>
                        {
                            ["label"] = new Dictionary<string, object?> { ["show"] = false }
                        }
                    }
                }
            });
    }

    private AutoVisualization? BuildTreemap(
        QueryResult result,
        DynChartDefinition definition,
        DynChartSeries measure)
    {
        var categoryField = definition.CategoryField;
        var groups = result.Rows
            .GroupBy(
                row => FormatCategory(QueryResult.Get(row, categoryField)),
                StringComparer.CurrentCultureIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
            {
                var rows = group.ToArray();
                var item = new Dictionary<string, object?>
                {
                    ["name"] = group.Key,
                    ["value"] = Aggregate(rows, measure.Field, measure.Aggregation)
                };

                if (TryChartColor(definition, rows, group.Key, out var color))
                    item["itemStyle"] = new Dictionary<string, object?> { ["color"] = color };

                return item;
            })
            .OrderByDescending(item => Math.Abs(Numeric(item["value"]) ?? 0d))
            .Take(Math.Clamp(definition.MaxPoints, 1, 500))
            .Cast<object>()
            .ToArray();

        if (groups.Length == 0)
            return null;

        return new AutoVisualization(
            "Treemap",
            $"{SeriesLabel(measure)} nach {categoryField}",
            new Dictionary<string, object?>
            {
                ["__dynItemFormat"] = measure.Format,
                ["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "item" },
                ["toolbox"] = Toolbox(),
                ["series"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["name"] = SeriesLabel(measure),
                        ["type"] = "treemap",
                        ["roam"] = false,
                        ["nodeClick"] = false,
                        ["breadcrumb"] = new Dictionary<string, object?> { ["show"] = false },
                        ["label"] = new Dictionary<string, object?>
                        {
                            ["show"] = true,
                            ["formatter"] = "{b}"
                        },
                        ["upperLabel"] = new Dictionary<string, object?> { ["show"] = false },
                        ["itemStyle"] = new Dictionary<string, object?>
                        {
                            ["borderColor"] = "#fff",
                            ["borderWidth"] = 3,
                            ["gapWidth"] = 3
                        },
                        ["data"] = groups
                    }
                }
            });
    }

    private bool TryChartColor(
        DynChartDefinition definition,
        IEnumerable<Dictionary<string, object?>> rows,
        string fallbackKey,
        out string color)
    {
        var row = rows.FirstOrDefault();
        var key = fallbackKey;

        if (row is not null
            && !string.IsNullOrWhiteSpace(definition.ColorKeyField))
        {
            var configured = FormatCategory(
                QueryResult.Get(row, definition.ColorKeyField));

            if (!string.IsNullOrWhiteSpace(configured))
                key = configured;
        }

        return themes.TryResolve(
            definition.ColorSet,
            key,
            out color,
            definition.ColorOverrides);
    }

    private static bool IsHexColor(string? color) =>
        !string.IsNullOrWhiteSpace(color)
        && color.Length == 7
        && color[0] == '#'
        && color.Skip(1).All(Uri.IsHexDigit);

    private static string SeriesLabel(DynChartSeries series) =>
        string.IsNullOrWhiteSpace(series.Label) ? series.Field : series.Label;

    private static double? Aggregate(
        IEnumerable<Dictionary<string, object?>> rows,
        string field,
        string aggregation)
    {
        var materialized = rows as IReadOnlyCollection<Dictionary<string, object?>>
            ?? rows.ToArray();

        if (aggregation.Equals("Count", StringComparison.OrdinalIgnoreCase))
            return materialized.Count;

        var values = materialized
            .Select(row => Numeric(QueryResult.Get(row, field)))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();

        if (values.Length == 0)
            return null;

        return aggregation.ToLowerInvariant() switch
        {
            "avg" or "average" => values.Average(),
            "min" => values.Min(),
            "max" => values.Max(),
            "first" or "none" => values[0],
            _ => values.Sum()
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

        if (value is IReadOnlyDictionary<string, object?> readOnly
            && readOnly.TryGetValue("value", out var readOnlyValue))
        {
            value = readOnlyValue;
        }
        else if (value is IDictionary<string, object?> dictionary
                 && dictionary.TryGetValue("value", out var dictionaryValue))
        {
            value = dictionaryValue;
        }

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

    private sealed class ChartCategoryComparer : IComparer<object?>
    {
        public static ChartCategoryComparer Instance { get; } = new();

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var dx = x switch
            {
                DateTime date => date,
                DateTimeOffset dto => dto.DateTime,
                DateOnly dateOnly => dateOnly.ToDateTime(TimeOnly.MinValue),
                _ => (DateTime?)null
            };
            var dy = y switch
            {
                DateTime date => date,
                DateTimeOffset dto => dto.DateTime,
                DateOnly dateOnly => dateOnly.ToDateTime(TimeOnly.MinValue),
                _ => (DateTime?)null
            };

            if (dx.HasValue && dy.HasValue)
                return dx.Value.CompareTo(dy.Value);

            if (x is IComparable comparable && x.GetType() == y.GetType())
                return comparable.CompareTo(y);

            return string.Compare(
                Convert.ToString(x, CultureInfo.GetCultureInfo("de-AT")),
                Convert.ToString(y, CultureInfo.GetCultureInfo("de-AT")),
                StringComparison.CurrentCultureIgnoreCase);
        }
    }

    private sealed record DataProfile(string? Date, string? Category, List<string> Numeric);
}
