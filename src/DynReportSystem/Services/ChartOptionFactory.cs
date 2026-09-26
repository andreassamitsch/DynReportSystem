using System.Globalization;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

public sealed class ChartOptionFactory(RdlStyleCatalog styles)
{
    private static readonly CultureInfo DeAt = CultureInfo.GetCultureInfo("de-AT");

    public object BuildDashboard(DashboardWidget widget, QueryResult result)
    {
        var series = widget.Series.Count > 0
            ? widget.Series
            : [new DashboardSeries
            {
                Name = widget.Title,
                Field = widget.ValueField ?? "",
                Type = widget.Type.Contains("line", StringComparison.OrdinalIgnoreCase) ? "line" : "bar",
                Aggregate = "sum",
                Format = widget.Format ?? "number"
            }];

        var points = Aggregate(widget, result, series);

        if (widget.Type.Equals("echarts-grouped-stack", StringComparison.OrdinalIgnoreCase))
            return GroupedStack(widget, result, series[0]);

        return widget.Type.ToLowerInvariant() switch
        {
            "echarts-donut" => Donut(widget, points, series[0]),
            "echarts-line" => Cartesian(widget, points, series, area: false),
            "echarts-area" => Cartesian(widget, points, series, area: true),
            "echarts-bar" => Cartesian(widget, points, series, area: false),
            "echarts-combo" => Cartesian(widget, points, series, area: false),
            _ => Cartesian(widget, points, series, area: false)
        };
    }

    public MetricSummary Summarize(DashboardWidget widget, QueryResult result)
    {
        var field = widget.ValueField ?? widget.Series.FirstOrDefault()?.Field;
        if (string.IsNullOrWhiteSpace(field))
            return new MetricSummary(null, null, null, null);

        var value = result.Rows.Sum(r => Decimal(r, field) ?? 0m);

        if (string.IsNullOrWhiteSpace(widget.CategoryField))
            return new MetricSummary(value, null, null, null);

        var oneSeries = new List<DashboardSeries>
        {
            new()
            {
                Name = widget.Title,
                Field = field,
                Aggregate = "sum",
                Type = "line",
                Format = widget.Format ?? "number"
            }
        };

        var points = Aggregate(widget, result, oneSeries);
        if (points.Count < 2)
            return new MetricSummary(value, points.LastOrDefault()?.Values.GetValueOrDefault(widget.Title), null, null);

        var current = points[^1].Values.GetValueOrDefault(widget.Title);
        var previous = points[^2].Values.GetValueOrDefault(widget.Title);
        decimal? trend = previous == 0
            ? null
            : (current - previous) / Math.Abs(previous) * 100m;

        return new MetricSummary(value, current, previous, trend);
    }

    public object Sparkline(DashboardWidget widget, QueryResult result)
    {
        var field = widget.ValueField ?? widget.Series.FirstOrDefault()?.Field ?? "";
        var series = new List<DashboardSeries>
        {
            new()
            {
                Name = widget.Title,
                Field = field,
                Type = "line",
                Aggregate = "sum",
                Format = widget.Format ?? "number"
            }
        };
        var points = Aggregate(widget, result, series);

        return new Dictionary<string, object?>
        {
            ["animation"] = true,
            ["grid"] = new Dictionary<string, object?> { ["left"] = 0, ["right"] = 0, ["top"] = 3, ["bottom"] = 0 },
            ["xAxis"] = new Dictionary<string, object?> { ["type"] = "category", ["show"] = false, ["data"] = points.Select(x => x.Label).ToArray() },
            ["yAxis"] = new Dictionary<string, object?> { ["type"] = "value", ["show"] = false, ["scale"] = true },
            ["tooltip"] = new Dictionary<string, object?> { ["show"] = false },
            ["series"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = widget.Title,
                    ["type"] = "line",
                    ["smooth"] = true,
                    ["symbol"] = "none",
                    ["lineStyle"] = new Dictionary<string, object?> { ["width"] = 2 },
                    ["areaStyle"] = new Dictionary<string, object?> { ["opacity"] = 0.08 },
                    ["data"] = points.Select(x => (object)x.Values.GetValueOrDefault(widget.Title)).ToArray()
                }
            }
        };
    }

    public IReadOnlyList<ChartDefinition> BuildDetail(string dataset, QueryResult result)
    {
        return dataset switch
        {
            "Umsatz" => RevenueDetail(result),
            "offenPosten" => OpenItemsDetail(result),
            "KundenReklamationen" => ComplaintsDetail(result),
            "LagerndeKundenartikel" => StockDetail(result),
            "offeneABs" => [],
            "offeneRAs" => [],
            "Bestelleingang" => OrderIntakeDetail(result),
            "Angebote" => OffersDetail(result),
            "AngebotsStatusAnalyse" =>
                [new ChartDefinition("offer-status-detail", "Angebotsstatus", "Verteilung nach Angebotswert",
                    SimpleDonut(result, "Status", "Angebotswert", "currency"))],
            "AngebotsAblehnungsgruende" =>
                [new ChartDefinition("offer-reject-detail", "Ablehnungsgründe", "Wert der abgelehnten Angebote",
                    SimpleBar(result, "Ablehnungsgrund", "Angebotswert", "currency", true, 12))],
            "AngebotsDurchlaufzeit" =>
                [new ChartDefinition("offer-lead-detail", "Angebotsdurchlaufzeit", "Durchschnittliche Arbeitstage im Zeitverlauf",
                    SimpleLine(result, "Monatsdatum", "DurchschnittArbeitstage", "number", average: true))],
            _ => []
        };
    }

    private static List<GroupPoint> Aggregate(
        DashboardWidget widget,
        QueryResult result,
        IReadOnlyList<DashboardSeries> series)
    {
        var categoryField = widget.CategoryField ?? "";
        var groups = new Dictionary<string, GroupAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in result.Rows)
        {
            var rawCategory = string.IsNullOrWhiteSpace(categoryField)
                ? "Gesamt"
                : QueryResult.Get(row, categoryField);

            var date = ToDate(rawCategory);
            var key = CategoryKey(rawCategory, date, widget.TimeBucket);
            var label = CategoryLabel(rawCategory, date, widget.TimeBucket);

            if (!groups.TryGetValue(key, out var group))
            {
                group = new GroupAccumulator(key, label, date);
                groups[key] = group;
            }

            foreach (var s in series)
            {
                var value = s.Aggregate.Equals("count", StringComparison.OrdinalIgnoreCase)
                    ? 1m
                    : Decimal(row, s.Field) ?? 0m;

                group.Add(s.Name, value);
            }
        }

        var list = groups.Values
            .Select(g =>
            {
                var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in series)
                {
                    var acc = g.Values.GetValueOrDefault(s.Name);
                    values[s.Name] = s.Aggregate.Equals("average", StringComparison.OrdinalIgnoreCase)
                        ? (acc.Count == 0 ? 0m : acc.Sum / acc.Count)
                        : acc.Sum;
                }

                return new GroupPoint(g.Key, g.Label, g.Date, values);
            })
            .ToList();

        if (list.Any(x => x.Date.HasValue))
            list = list.OrderBy(x => x.Date ?? DateTime.MaxValue).ToList();
        else if (widget.Sort.Equals("value-desc", StringComparison.OrdinalIgnoreCase) && series.Count > 0)
            list = list.OrderByDescending(x => x.Values.GetValueOrDefault(series[0].Name)).ToList();
        else
            list = list.OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).ToList();

        if (widget.Limit > 0 && list.Count > widget.Limit)
            list = list.Take(widget.Limit).ToList();

        return list;
    }

    private static object Cartesian(
        DashboardWidget widget,
        IReadOnlyList<GroupPoint> points,
        IReadOnlyList<DashboardSeries> series,
        bool area)
    {
        var horizontal = widget.Horizontal;
        var categories = points.Select(x => x.Label).ToArray();
        var formats = series.ToDictionary(x => x.Name, x => x.Format, StringComparer.OrdinalIgnoreCase);

        var chartSeries = series.Select((s, index) =>
        {
            var type = string.IsNullOrWhiteSpace(s.Type) ? "bar" : s.Type;
            var data = points.Select(p => (object)new Dictionary<string, object?>
            {
                ["value"] = p.Values.GetValueOrDefault(s.Name),
                ["categoryKey"] = p.Key,
                ["categoryLabel"] = p.Label
            }).ToArray();

            var entry = new Dictionary<string, object?>
            {
                ["name"] = s.Name,
                ["type"] = type,
                ["data"] = data,
                ["smooth"] = type.Equals("line", StringComparison.OrdinalIgnoreCase),
                ["symbolSize"] = 7,
                ["emphasis"] = new Dictionary<string, object?> { ["focus"] = "series" }
            };

            if (!string.IsNullOrWhiteSpace(s.Stack))
                entry["stack"] = s.Stack;

            var itemStyle = new Dictionary<string, object?>();

            if (type.Equals("bar", StringComparison.OrdinalIgnoreCase))
            {
                entry["barMaxWidth"] = 30;
                itemStyle["borderRadius"] = horizontal ? new[] { 0, 5, 5, 0 } : new[] { 4, 4, 0, 0 };
            }

            if (!string.IsNullOrWhiteSpace(s.Color))
            {
                itemStyle["color"] = s.Color;
                entry["lineStyle"] = new Dictionary<string, object?> { ["color"] = s.Color, ["width"] = 2.5 };
            }

            if (itemStyle.Count > 0)
                entry["itemStyle"] = itemStyle;

            if (area || s.Area)
                entry["areaStyle"] = new Dictionary<string, object?> { ["opacity"] = 0.15 };

            if (s.Axis > 0)
                entry["yAxisIndex"] = s.Axis;

            return (object)entry;
        }).ToArray();

        var categoryAxis = new Dictionary<string, object?>
        {
            ["type"] = "category",
            ["data"] = categories,
            ["axisTick"] = new Dictionary<string, object?> { ["show"] = false },
            ["axisLine"] = new Dictionary<string, object?> { ["lineStyle"] = new Dictionary<string, object?> { ["color"] = "#d8e2e5" } },
            ["axisLabel"] = new Dictionary<string, object?>
            {
                ["color"] = "#71848c",
                ["hideOverlap"] = true,
                ["interval"] = 0,
                ["rotate"] = horizontal ? 0 : (categories.Length > 14 ? 35 : 0)
            }
        };

        var valueFormat = series.FirstOrDefault()?.Format ?? "number";
        var valueAxis = new Dictionary<string, object?>
        {
            ["type"] = "value",
            ["scale"] = false,
            ["splitLine"] = new Dictionary<string, object?> { ["lineStyle"] = new Dictionary<string, object?> { ["color"] = "#edf2f3" } },
            ["axisLabel"] = new Dictionary<string, object?> { ["color"] = "#71848c" },
            ["__dynFormat"] = valueFormat
        };

        var option = BaseOption();
        option["__dynSeriesFormats"] = formats;
        option["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "axis", ["axisPointer"] = new Dictionary<string, object?> { ["type"] = "shadow" } };
        option["legend"] = new Dictionary<string, object?> { ["top"] = 0, ["right"] = 0, ["textStyle"] = new Dictionary<string, object?> { ["color"] = "#5f747c" } };
        option["grid"] = new Dictionary<string, object?> { ["left"] = horizontal ? 125 : 55, ["right"] = 30, ["top"] = 48, ["bottom"] = categories.Length > 14 ? 65 : 45, ["containLabel"] = true };
        option["xAxis"] = horizontal ? valueAxis : categoryAxis;
        option["yAxis"] = horizontal ? categoryAxis : valueAxis;
        option["series"] = chartSeries;
        option["toolbox"] = Toolbox();
        option["__dynResponsive"] = horizontal
            ? "horizontal-bar"
            : widget.Id.Equals("umsatzverlauf", StringComparison.OrdinalIgnoreCase)
                ? "primary-stack"
                : "cartesian";

        if (!horizontal && categories.Length > 18)
        {
            option["dataZoom"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "inside", ["start"] = 0, ["end"] = 70 },
                new Dictionary<string, object?> { ["type"] = "slider", ["height"] = 14, ["bottom"] = 3 }
            };
        }

        return option;
    }

    private object GroupedStack(
        DashboardWidget widget,
        QueryResult result,
        DashboardSeries template)
    {
        if (string.IsNullOrWhiteSpace(widget.CategoryField)
            || string.IsNullOrWhiteSpace(widget.GroupField)
            || string.IsNullOrWhiteSpace(template.Field))
            return new Dictionary<string, object?>();

        var groups = new Dictionary<string, DynamicGroup>(StringComparer.OrdinalIgnoreCase);
        var categories = new Dictionary<string, (string Label, DateTime? Date)>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in result.Rows)
        {
            var rawCategory = QueryResult.Get(row, widget.CategoryField);
            var date = ToDate(rawCategory);
            var categoryKey = CategoryKey(rawCategory, date, widget.TimeBucket);
            var categoryLabel = CategoryLabel(rawCategory, date, widget.TimeBucket);

            var groupLabel = Text(row, widget.GroupField);
            if (string.IsNullOrWhiteSpace(groupLabel))
                groupLabel = "Ohne Zuordnung";

            var colorKey = string.IsNullOrWhiteSpace(widget.GroupColorField)
                ? groupLabel
                : Text(row, widget.GroupColorField);

            if (!groups.TryGetValue(groupLabel, out var group))
            {
                group = new DynamicGroup(groupLabel, colorKey, styles.BusinessAreaColor(colorKey));
                groups[groupLabel] = group;
            }

            categories[categoryKey] = (categoryLabel, date);
            group.Values[categoryKey] = group.Values.GetValueOrDefault(categoryKey) + (Decimal(row, template.Field) ?? 0m);
        }

        var orderedCategories = categories
            .Select(x => new { Key = x.Key, x.Value.Label, x.Value.Date })
            .OrderBy(x => x.Date ?? DateTime.MaxValue)
            .ThenBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var orderedGroups = groups.Values
            .OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var formatMap = orderedGroups.ToDictionary(x => x.Label, _ => template.Format, StringComparer.OrdinalIgnoreCase);

        var series = orderedGroups.Select(group =>
        {
            var type = string.IsNullOrWhiteSpace(template.Type) ? "bar" : template.Type;
            var entry = new Dictionary<string, object?>
            {
                ["name"] = group.Label,
                ["type"] = type,
                ["stack"] = string.IsNullOrWhiteSpace(template.Stack) ? "business-area" : template.Stack,
                ["smooth"] = type.Equals("line", StringComparison.OrdinalIgnoreCase),
                ["symbol"] = type.Equals("line", StringComparison.OrdinalIgnoreCase) ? "none" : null,
                ["emphasis"] = new Dictionary<string, object?> { ["focus"] = "series" },
                ["itemStyle"] = new Dictionary<string, object?> { ["color"] = group.Color },
                ["lineStyle"] = new Dictionary<string, object?> { ["color"] = group.Color, ["width"] = 2 },
                ["data"] = orderedCategories.Select(category => (object)new Dictionary<string, object?>
                {
                    ["value"] = group.Values.GetValueOrDefault(category.Key),
                    ["categoryKey"] = category.Key,
                    ["categoryLabel"] = category.Label,
                    ["groupLabel"] = group.Label,
                    ["groupColorKey"] = group.ColorKey
                }).ToArray()
            };

            if (template.Area || type.Equals("line", StringComparison.OrdinalIgnoreCase))
                entry["areaStyle"] = new Dictionary<string, object?> { ["opacity"] = 0.72 };

            if (type.Equals("bar", StringComparison.OrdinalIgnoreCase))
                entry["barMaxWidth"] = 34;

            return (object)entry;
        }).ToArray();

        var option = BaseOption();
        option["__dynSeriesFormats"] = formatMap;
        option["__dynResponsive"] = "grouped-stack";
        option["tooltip"] = new Dictionary<string, object?>
        {
            ["trigger"] = "axis",
            ["axisPointer"] = new Dictionary<string, object?> { ["type"] = "shadow" }
        };
        option["legend"] = new Dictionary<string, object?>
        {
            ["type"] = "scroll",
            ["top"] = 0,
            ["left"] = 0,
            ["right"] = 42,
            ["textStyle"] = new Dictionary<string, object?> { ["color"] = "#5f747c" }
        };
        option["grid"] = new Dictionary<string, object?>
        {
            ["left"] = 58,
            ["right"] = 22,
            ["top"] = 54,
            ["bottom"] = orderedCategories.Length > 14 ? 68 : 44,
            ["containLabel"] = true
        };
        option["xAxis"] = new Dictionary<string, object?>
        {
            ["type"] = "category",
            ["data"] = orderedCategories.Select(x => x.Label).ToArray(),
            ["axisTick"] = new Dictionary<string, object?> { ["show"] = false },
            ["axisLabel"] = new Dictionary<string, object?> { ["hideOverlap"] = true, ["color"] = "#71848c" }
        };
        option["yAxis"] = new Dictionary<string, object?>
        {
            ["type"] = "value",
            ["__dynFormat"] = template.Format,
            ["splitLine"] = new Dictionary<string, object?> { ["lineStyle"] = new Dictionary<string, object?> { ["color"] = "#edf2f3" } },
            ["axisLabel"] = new Dictionary<string, object?> { ["color"] = "#71848c" }
        };
        option["series"] = series;
        option["toolbox"] = Toolbox();

        if (orderedCategories.Length > 14)
        {
            option["dataZoom"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "inside", ["start"] = 0, ["end"] = 75 },
                new Dictionary<string, object?> { ["type"] = "slider", ["height"] = 14, ["bottom"] = 3 }
            };
        }

        return option;
    }

    private static object Donut(
        DashboardWidget widget,
        IReadOnlyList<GroupPoint> points,
        DashboardSeries series)
    {
        var data = points.Select(p => (object)new Dictionary<string, object?>
        {
            ["name"] = p.Label,
            ["value"] = p.Values.GetValueOrDefault(series.Name),
            ["categoryKey"] = p.Key
        }).ToArray();

        var option = BaseOption();
        option["__dynItemFormat"] = series.Format;
        option["__dynResponsive"] = "donut";
        option["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "item" };
        option["legend"] = new Dictionary<string, object?> { ["type"] = "scroll", ["bottom"] = 0, ["textStyle"] = new Dictionary<string, object?> { ["color"] = "#61767e" } };
        option["toolbox"] = Toolbox();
        option["series"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = series.Name,
                ["type"] = "pie",
                ["radius"] = new[] { "48%", "72%" },
                ["center"] = new[] { "50%", "44%" },
                ["avoidLabelOverlap"] = true,
                ["itemStyle"] = new Dictionary<string, object?> { ["borderColor"] = "#fff", ["borderWidth"] = 3, ["borderRadius"] = 6 },
                ["label"] = new Dictionary<string, object?> { ["show"] = false },
                ["emphasis"] = new Dictionary<string, object?>
                {
                    ["label"] = new Dictionary<string, object?> { ["show"] = true, ["fontSize"] = 14, ["fontWeight"] = "bold" }
                },
                ["data"] = data
            }
        };
        return option;
    }

    private static Dictionary<string, object?> BaseOption() => new()
    {
        ["animationDuration"] = 500,
        ["textStyle"] = new Dictionary<string, object?> { ["fontFamily"] = "Inter, Segoe UI, Arial, sans-serif" }
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

    public ChartDefinition? BuildOpenOrderBusinessArea(QueryResult openOrders, QueryResult frameworkOrders)
    {
        var values = new Dictionary<string, BusinessAreaOpenValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in openOrders.Rows)
        {
            var label = Text(row, "GeschäftsbereichBezeichnung");
            if (string.IsNullOrWhiteSpace(label)) label = Text(row, "Geschäftsbereich");
            if (string.IsNullOrWhiteSpace(label)) label = "Ohne Zuordnung";

            var colorKey = Text(row, "Geschäftsbereich");
            if (!values.TryGetValue(label, out var value))
                value = new BusinessAreaOpenValue(colorKey);

            value.OpenOrders += Decimal(row, "BestellwertInklZuAbschlag") ?? 0m;
            if (string.IsNullOrWhiteSpace(value.ColorKey) && !string.IsNullOrWhiteSpace(colorKey))
                value.ColorKey = colorKey;

            values[label] = value;
        }

        foreach (var row in frameworkOrders.Rows)
        {
            var label = Text(row, "GeschäftsbereichBezeichnung");
            if (string.IsNullOrWhiteSpace(label)) label = Text(row, "Geschäftsbereich");
            if (string.IsNullOrWhiteSpace(label)) label = "Ohne Zuordnung";

            var colorKey = Text(row, "Geschäftsbereich");
            if (!values.TryGetValue(label, out var value))
                value = new BusinessAreaOpenValue(colorKey);

            value.Framework += Decimal(row, "OffenerAbrufwertRA") ?? 0m;
            if (string.IsNullOrWhiteSpace(value.ColorKey) && !string.IsNullOrWhiteSpace(colorKey))
                value.ColorKey = colorKey;

            values[label] = value;
        }

        if (values.Count == 0)
            return null;

        var ordered = values
            .OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var openData = ordered.Select(x =>
        {
            var color = styles.BusinessAreaColor(x.Value.ColorKey);
            return (object)new Dictionary<string, object?>
            {
                ["value"] = x.Value.OpenOrders,
                ["categoryLabel"] = x.Key,
                ["groupLabel"] = x.Key,
                ["itemStyle"] = new Dictionary<string, object?> { ["color"] = color }
            };
        }).ToArray();

        var frameworkData = ordered.Select(x =>
        {
            var color = styles.MixWithWhite(styles.BusinessAreaColor(x.Value.ColorKey), 58);
            return (object)new Dictionary<string, object?>
            {
                ["value"] = x.Value.Framework,
                ["categoryLabel"] = x.Key,
                ["groupLabel"] = x.Key,
                ["itemStyle"] = new Dictionary<string, object?> { ["color"] = color }
            };
        }).ToArray();

        var option = BaseOption();
        option["__dynSeriesFormats"] = new Dictionary<string, string>
        {
            ["Offene AB"] = "currency",
            ["Rahmen"] = "currency"
        };
        option["__dynResponsive"] = "horizontal-stack";
        option["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "axis", ["axisPointer"] = new Dictionary<string, object?> { ["type"] = "shadow" } };
        option["legend"] = new Dictionary<string, object?> { ["top"] = 0, ["left"] = 0 };
        option["grid"] = new Dictionary<string, object?> { ["left"] = 155, ["right"] = 28, ["top"] = 45, ["bottom"] = 35, ["containLabel"] = true };
        option["xAxis"] = new Dictionary<string, object?> { ["type"] = "value", ["__dynFormat"] = "currency", ["splitLine"] = new Dictionary<string, object?> { ["lineStyle"] = new Dictionary<string, object?> { ["color"] = "#edf2f3" } } };
        option["yAxis"] = new Dictionary<string, object?> { ["type"] = "category", ["data"] = ordered.Select(x => x.Key).ToArray(), ["axisTick"] = new Dictionary<string, object?> { ["show"] = false } };
        option["toolbox"] = Toolbox();
        option["series"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = "Offene AB",
                ["type"] = "bar",
                ["stack"] = "open-orders",
                ["barMaxWidth"] = 32,
                ["data"] = openData
            },
            new Dictionary<string, object?>
            {
                ["name"] = "Rahmen",
                ["type"] = "bar",
                ["stack"] = "open-orders",
                ["barMaxWidth"] = 32,
                ["data"] = frameworkData
            }
        };

        return new ChartDefinition(
            "open-orders-business-stack",
            "Offene AB & Rahmen nach Geschäftsbereich",
            "Gestapelte Werte · Farben aus der RDL",
            option,
            "390px");
    }

    private ChartDefinition? BuildBusinessAreaTimeStack(
        QueryResult result,
        string key,
        string title,
        string subtitle,
        string dateField,
        string valueField,
        string groupField,
        string groupColorField,
        string chartType,
        bool area,
        string height)
    {
        if (result.Rows.Count == 0)
            return null;

        var widget = new DashboardWidget
        {
            Id = key,
            Type = "echarts-grouped-stack",
            CategoryField = dateField,
            GroupField = groupField,
            GroupColorField = groupColorField,
            TimeBucket = "month"
        };

        var template = new DashboardSeries
        {
            Name = title,
            Field = valueField,
            Type = chartType,
            Aggregate = "sum",
            Format = "currency",
            Stack = "business-area",
            Area = area
        };

        var option = GroupedStack(widget, result, template);
        return new ChartDefinition(key, title, subtitle, option, height);
    }

    private IReadOnlyList<ChartDefinition> RevenueDetail(QueryResult result)
    {
        var country = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var area = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in result.Rows)
        {
            var amount = Decimal(row, "Betrag") ?? 0m;
            var iso = Text(row, "Land").Trim().ToUpperInvariant();
            var business = Text(row, "Geschäftsbereich");

            if (iso.Length == 2)
                country[iso] = country.GetValueOrDefault(iso) + amount;

            if (!string.IsNullOrWhiteSpace(business))
                area[business] = area.GetValueOrDefault(business) + amount;
        }

        var charts = new List<ChartDefinition>();

        var businessAreaTrend = BuildBusinessAreaTimeStack(
            result,
            key: "revenue-business-area-month",
            title: "Umsatz nach Geschäftsbereich",
            subtitle: "Gestapelte Monatsentwicklung · GB-Farben aus der RDL",
            dateField: "Belegdatum",
            valueField: "Betrag",
            groupField: "Geschäftsbereich",
            groupColorField: "Geschäftsbereich_Nr",
            chartType: "line",
            area: true,
            height: "400px");

        if (businessAreaTrend is not null)
            charts.Add(businessAreaTrend);

        if (country.Count > 0)
        {
            var data = country.Select(x => (object)new Dictionary<string, object?> { ["name"] = x.Key, ["value"] = x.Value }).ToArray();
            var max = country.Values.Select(Math.Abs).DefaultIfEmpty(1m).Max();

            charts.Add(new ChartDefinition(
                "revenue-world",
                "Umsatz nach Land",
                "Interaktive Weltkarte · Landcode aus Oxaion",
                new Dictionary<string, object?>
                {
                    ["__dynMap"] = "world",
                    ["__dynItemFormat"] = "currency",
                    ["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "item" },
                    ["visualMap"] = new Dictionary<string, object?>
                    {
                        ["min"] = 0,
                        ["max"] = max,
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
                            ["name"] = "Umsatz",
                            ["type"] = "map",
                            ["map"] = "dyn-world",
                            ["roam"] = true,
                            ["selectedMode"] = false,
                            ["data"] = data,
                            ["emphasis"] = new Dictionary<string, object?> { ["label"] = new Dictionary<string, object?> { ["show"] = false } }
                        }
                    }
                },
                "430px"));
        }

        if (area.Count > 0)
        {
            var data = area.OrderByDescending(x => x.Value)
                .Select(x => (object)new Dictionary<string, object?> { ["name"] = x.Key, ["value"] = x.Value })
                .ToArray();

            charts.Add(new ChartDefinition(
                "revenue-area",
                "Umsatzmix",
                "Anteil nach Geschäftsbereich",
                new Dictionary<string, object?>
                {
                    ["__dynItemFormat"] = "currency",
                    ["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "item" },
                    ["toolbox"] = Toolbox(),
                    ["series"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "treemap",
                            ["roam"] = false,
                            ["nodeClick"] = false,
                            ["breadcrumb"] = new Dictionary<string, object?> { ["show"] = false },
                            ["label"] = new Dictionary<string, object?> { ["show"] = true, ["formatter"] = "{b}" },
                            ["upperLabel"] = new Dictionary<string, object?> { ["show"] = false },
                            ["itemStyle"] = new Dictionary<string, object?> { ["borderColor"] = "#fff", ["borderWidth"] = 3, ["gapWidth"] = 3 },
                            ["data"] = data
                        }
                    }
                }));
        }

        return charts;
    }

    private static IReadOnlyList<ChartDefinition> OpenItemsDetail(QueryResult result)
    {
        var labels = new[] { "Nicht fällig", "1–30 Tage", "31–60 Tage", "61–90 Tage", "> 90 Tage" };
        var amounts = labels.ToDictionary(x => x, _ => 0m);
        var counts = labels.ToDictionary(x => x, _ => 0);

        foreach (var row in result.Rows)
        {
            var due = ToDate(QueryResult.Get(row, "Fällig_am"));
            var amount = Decimal(row, "Betrag") ?? 0m;
            if (!due.HasValue) continue;

            var days = (DateTime.Today - due.Value.Date).Days;
            var bucket = days <= 0 ? labels[0] : days <= 30 ? labels[1] : days <= 60 ? labels[2] : days <= 90 ? labels[3] : labels[4];
            amounts[bucket] += amount;
            counts[bucket]++;
        }

        var option = BaseOption();
        option["__dynSeriesFormats"] = new Dictionary<string, string> { ["Offener Betrag"] = "currency", ["Posten"] = "integer" };
        option["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "axis" };
        option["legend"] = new Dictionary<string, object?> { ["top"] = 0, ["right"] = 0 };
        option["grid"] = new Dictionary<string, object?> { ["left"] = 50, ["right"] = 55, ["top"] = 45, ["bottom"] = 40, ["containLabel"] = true };
        option["xAxis"] = new Dictionary<string, object?> { ["type"] = "category", ["data"] = labels };
        option["yAxis"] = new object[]
        {
            new Dictionary<string, object?> { ["type"] = "value", ["__dynFormat"] = "currency", ["splitLine"] = new Dictionary<string, object?> { ["lineStyle"] = new Dictionary<string, object?> { ["color"] = "#edf2f3" } } },
            new Dictionary<string, object?> { ["type"] = "value", ["__dynFormat"] = "integer", ["splitLine"] = new Dictionary<string, object?> { ["show"] = false } }
        };
        option["series"] = new object[]
        {
            new Dictionary<string, object?> { ["name"] = "Offener Betrag", ["type"] = "bar", ["barMaxWidth"] = 42, ["data"] = labels.Select(x => (object)amounts[x]).ToArray(), ["itemStyle"] = new Dictionary<string, object?> { ["borderRadius"] = new[] { 6,6,0,0 } } },
            new Dictionary<string, object?> { ["name"] = "Posten", ["type"] = "line", ["smooth"] = true, ["yAxisIndex"] = 1, ["data"] = labels.Select(x => (object)counts[x]).ToArray() }
        };
        option["toolbox"] = Toolbox();

        return [new ChartDefinition("op-aging", "OP-Aging", "Offene Posten nach Fälligkeit · Betrag und Anzahl", option, "360px")];
    }

    private static IReadOnlyList<ChartDefinition> ComplaintsDetail(QueryResult result)
    {
        var groups = new SortedDictionary<DateTime, (decimal Cost, int Count)>();

        foreach (var row in result.Rows)
        {
            var date = ToDate(QueryResult.Get(row, "ReklamationEröffnet")) ?? ToDate(QueryResult.Get(row, "ReklamationAngelegt"));
            if (!date.HasValue) continue;
            var month = new DateTime(date.Value.Year, date.Value.Month, 1);
            var current = groups.GetValueOrDefault(month);
            groups[month] = (current.Cost + (Decimal(row, "ReklamationKosten") ?? 0m), current.Count + 1);
        }

        if (groups.Count == 0) return [];

        var labels = groups.Keys.Select(x => x.ToString("MM/yy", DeAt)).ToArray();
        var option = BaseOption();
        option["__dynSeriesFormats"] = new Dictionary<string, string> { ["Kosten"] = "currency", ["Reklamationen"] = "integer" };
        option["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "axis" };
        option["legend"] = new Dictionary<string, object?> { ["top"] = 0, ["right"] = 0 };
        option["grid"] = new Dictionary<string, object?> { ["left"] = 50, ["right"] = 55, ["top"] = 45, ["bottom"] = 45, ["containLabel"] = true };
        option["xAxis"] = new Dictionary<string, object?> { ["type"] = "category", ["data"] = labels };
        option["yAxis"] = new object[]
        {
            new Dictionary<string, object?> { ["type"] = "value", ["__dynFormat"] = "currency" },
            new Dictionary<string, object?> { ["type"] = "value", ["__dynFormat"] = "integer", ["splitLine"] = new Dictionary<string, object?> { ["show"] = false } }
        };
        option["series"] = new object[]
        {
            new Dictionary<string, object?> { ["name"] = "Kosten", ["type"] = "bar", ["barMaxWidth"] = 30, ["data"] = groups.Values.Select(x => (object)x.Cost).ToArray() },
            new Dictionary<string, object?> { ["name"] = "Reklamationen", ["type"] = "line", ["smooth"] = true, ["yAxisIndex"] = 1, ["data"] = groups.Values.Select(x => (object)x.Count).ToArray() }
        };
        option["toolbox"] = Toolbox();

        return [new ChartDefinition("complaints-trend", "Reklamationen im Verlauf", "Anzahl und Reklamationskosten je Monat", option, "360px")];
    }

    private static IReadOnlyList<ChartDefinition> StockDetail(QueryResult result)
    {
        var status = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var customers = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in result.Rows)
        {
            var value = Decimal(row, "Lagerwert") ?? 0m;
            var s = Text(row, "Status");
            var c = Text(row, "Kunde");

            if (!string.IsNullOrWhiteSpace(s))
                status[s] = status.GetValueOrDefault(s) + value;
            if (!string.IsNullOrWhiteSpace(c))
                customers[c] = customers.GetValueOrDefault(c) + value;
        }

        var charts = new List<ChartDefinition>();
        if (status.Count > 0)
        {
            charts.Add(new ChartDefinition(
                "stock-status",
                "Lagerwert nach Status",
                "Verteilung der lagernden Kundenartikel",
                DictionaryDonut(status, "Lagerwert", "currency")));
        }

        if (customers.Count > 0)
        {
            charts.Add(new ChartDefinition(
                "stock-customers",
                "Größte gebundene Lagerwerte",
                "Top-Kunden nach Lagerwert",
                DictionaryBar(customers, "Lagerwert", "currency", 12),
                "390px"));
        }

        return charts;
    }

    private IReadOnlyList<ChartDefinition> OpenOrdersDetail(QueryResult result, bool framework)
    {
        var field = framework ? "OffenerAbrufwertRA" : "BestellwertInklZuAbschlag";
        var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in result.Rows)
        {
            var name = Text(row, "GeschäftsbereichBezeichnung");
            if (string.IsNullOrWhiteSpace(name)) name = Text(row, "Geschäftsbereich");
            if (string.IsNullOrWhiteSpace(name)) name = "Ohne Zuordnung";
            values[name] = values.GetValueOrDefault(name) + (Decimal(row, field) ?? 0m);
        }

        return values.Count == 0 ? [] :
            [new ChartDefinition(
                framework ? "ra-business" : "ab-business",
                framework ? "Offene Rahmenabrufe" : "Offene Aufträge",
                "Wert nach Geschäftsbereich",
                DictionaryBar(values, "Wert", "currency", 12),
                "360px")];
    }

    private IReadOnlyList<ChartDefinition> OrderIntakeDetail(QueryResult result)
    {
        var charts = new List<ChartDefinition>();

        var stack = BuildBusinessAreaTimeStack(
            result,
            key: "order-intake-business-month",
            title: "Bestelleingang nach Geschäftsbereich",
            subtitle: "Gestapelte Monatswerte · GB-Farben aus der RDL",
            dateField: "Datum",
            valueField: "BestellwertInklZuAbschlag",
            groupField: "Geschäftsbereich",
            groupColorField: "Geschäftsbereich_Nr",
            chartType: "bar",
            area: false,
            height: "390px");

        if (stack is not null)
            charts.Add(stack);

        var month = new SortedDictionary<DateTime, decimal>();
        foreach (var row in result.Rows)
        {
            var value = Decimal(row, "BestellwertInklZuAbschlag") ?? 0m;
            var date = ToDate(QueryResult.Get(row, "Monatsdatum")) ?? ToDate(QueryResult.Get(row, "Datum"));
            if (!date.HasValue) continue;

            var m = new DateTime(date.Value.Year, date.Value.Month, 1);
            month[m] = month.GetValueOrDefault(m) + value;
        }

        if (month.Count > 0)
        {
            charts.Add(new ChartDefinition(
                "order-intake-total",
                "Bestelleingang gesamt",
                "Gesamtwert je Monat",
                DictionaryTimeLine(month, "Bestelleingang", "currency", true),
                "310px"));
        }

        return charts;
    }

    private static IReadOnlyList<ChartDefinition> OffersDetail(QueryResult result)
    {
        var status = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var business = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in result.Rows)
        {
            var value = Decimal(row, "BestellwertInklZuAbschlag") ?? Decimal(row, "Bestellwert") ?? 0m;
            var s = Text(row, "Status");
            var b = Text(row, "GeschäftsbereichBezeichnung");

            if (!string.IsNullOrWhiteSpace(s))
                status[s] = status.GetValueOrDefault(s) + value;
            if (!string.IsNullOrWhiteSpace(b))
                business[b] = business.GetValueOrDefault(b) + value;
        }

        var charts = new List<ChartDefinition>();
        if (status.Count > 0)
            charts.Add(new ChartDefinition("offers-status-raw", "Angebote nach Status", "Angebotswert", DictionaryDonut(status, "Angebotswert", "currency")));
        if (business.Count > 0)
            charts.Add(new ChartDefinition("offers-business", "Angebote nach Geschäftsbereich", "Angebotswert", DictionaryBar(business, "Angebotswert", "currency", 12)));
        return charts;
    }

    private static object SimpleDonut(QueryResult result, string category, string value, string format)
    {
        var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in result.Rows)
        {
            var key = Text(row, category);
            if (!string.IsNullOrWhiteSpace(key))
                dict[key] = dict.GetValueOrDefault(key) + (Decimal(row, value) ?? 0m);
        }
        return DictionaryDonut(dict, value, format);
    }

    private static object SimpleBar(QueryResult result, string category, string value, string format, bool horizontal, int limit)
    {
        var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in result.Rows)
        {
            var key = Text(row, category);
            if (!string.IsNullOrWhiteSpace(key))
                dict[key] = dict.GetValueOrDefault(key) + (Decimal(row, value) ?? 0m);
        }
        return DictionaryBar(dict, value, format, limit);
    }

    private static object SimpleLine(QueryResult result, string category, string value, string format, bool average)
    {
        var dict = new SortedDictionary<DateTime, (decimal Sum, int Count)>();
        foreach (var row in result.Rows)
        {
            var date = ToDate(QueryResult.Get(row, category));
            if (!date.HasValue) continue;
            var month = new DateTime(date.Value.Year, date.Value.Month, 1);
            var current = dict.GetValueOrDefault(month);
            dict[month] = (current.Sum + (Decimal(row, value) ?? 0m), current.Count + 1);
        }

        var values = dict.ToDictionary(x => x.Key, x => average && x.Value.Count > 0 ? x.Value.Sum / x.Value.Count : x.Value.Sum);
        return DictionaryTimeLine(values, value, format, false);
    }

    private static object DictionaryDonut(IReadOnlyDictionary<string, decimal> values, string name, string format)
    {
        var data = values.OrderByDescending(x => Math.Abs(x.Value))
            .Select(x => (object)new Dictionary<string, object?> { ["name"] = x.Key, ["value"] = x.Value })
            .ToArray();

        return new Dictionary<string, object?>
        {
            ["__dynItemFormat"] = format,
            ["__dynResponsive"] = "donut",
            ["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "item" },
            ["legend"] = new Dictionary<string, object?> { ["type"] = "scroll", ["bottom"] = 0 },
            ["toolbox"] = Toolbox(),
            ["series"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["type"] = "pie",
                    ["radius"] = new[] { "48%", "72%" },
                    ["center"] = new[] { "50%", "44%" },
                    ["itemStyle"] = new Dictionary<string, object?> { ["borderColor"] = "#fff", ["borderWidth"] = 3, ["borderRadius"] = 5 },
                    ["label"] = new Dictionary<string, object?> { ["show"] = false },
                    ["data"] = data
                }
            }
        };
    }

    private static object DictionaryBar(IReadOnlyDictionary<string, decimal> values, string name, string format, int limit)
    {
        var points = values.OrderByDescending(x => Math.Abs(x.Value)).Take(limit).Reverse().ToArray();

        return new Dictionary<string, object?>
        {
            ["__dynSeriesFormats"] = new Dictionary<string, string> { [name] = format },
            ["__dynResponsive"] = "horizontal-bar",
            ["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "axis" },
            ["grid"] = new Dictionary<string, object?> { ["left"] = 135, ["right"] = 30, ["top"] = 25, ["bottom"] = 35, ["containLabel"] = true },
            ["xAxis"] = new Dictionary<string, object?> { ["type"] = "value", ["__dynFormat"] = format, ["splitLine"] = new Dictionary<string, object?> { ["lineStyle"] = new Dictionary<string, object?> { ["color"] = "#edf2f3" } } },
            ["yAxis"] = new Dictionary<string, object?> { ["type"] = "category", ["data"] = points.Select(x => x.Key).ToArray(), ["axisTick"] = new Dictionary<string, object?> { ["show"] = false } },
            ["toolbox"] = Toolbox(),
            ["series"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["type"] = "bar",
                    ["barMaxWidth"] = 28,
                    ["itemStyle"] = new Dictionary<string, object?> { ["borderRadius"] = new[] { 0,6,6,0 } },
                    ["data"] = points.Select(x => (object)x.Value).ToArray()
                }
            }
        };
    }

    private static object DictionaryTimeLine(IReadOnlyDictionary<DateTime, decimal> values, string name, string format, bool area)
    {
        var ordered = values.OrderBy(x => x.Key).ToArray();
        return new Dictionary<string, object?>
        {
            ["__dynSeriesFormats"] = new Dictionary<string, string> { [name] = format },
            ["__dynResponsive"] = "cartesian",
            ["tooltip"] = new Dictionary<string, object?> { ["trigger"] = "axis" },
            ["grid"] = new Dictionary<string, object?> { ["left"] = 55, ["right"] = 25, ["top"] = 25, ["bottom"] = 45, ["containLabel"] = true },
            ["xAxis"] = new Dictionary<string, object?> { ["type"] = "category", ["data"] = ordered.Select(x => x.Key.ToString("MM/yy", DeAt)).ToArray() },
            ["yAxis"] = new Dictionary<string, object?> { ["type"] = "value", ["__dynFormat"] = format, ["splitLine"] = new Dictionary<string, object?> { ["lineStyle"] = new Dictionary<string, object?> { ["color"] = "#edf2f3" } } },
            ["toolbox"] = Toolbox(),
            ["series"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["type"] = "line",
                    ["smooth"] = true,
                    ["symbolSize"] = 7,
                    ["areaStyle"] = area ? new Dictionary<string, object?> { ["opacity"] = 0.12 } : null,
                    ["data"] = ordered.Select(x => (object)x.Value).ToArray()
                }
            }
        };
    }

    private static decimal? Decimal(IReadOnlyDictionary<string, object?> row, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var value = QueryResult.Get(row, key);
        if (value is null) return null;
        try { return Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    private static string Text(IReadOnlyDictionary<string, object?> row, string key) =>
        Convert.ToString(QueryResult.Get(row, key), DeAt)?.Trim() ?? "";

    private static DateTime? ToDate(object? value)
    {
        if (value is DateTime date) return date;
        if (value is DateTimeOffset dto) return dto.DateTime;
        if (value is null) return null;
        return DateTime.TryParse(Convert.ToString(value, DeAt), DeAt, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed
            : DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed)
                ? parsed
                : null;
    }

    private static string CategoryKey(object? raw, DateTime? date, string? bucket)
    {
        if (date.HasValue && string.Equals(bucket, "month", StringComparison.OrdinalIgnoreCase))
            return new DateTime(date.Value.Year, date.Value.Month, 1).ToString("yyyy-MM-dd");
        if (date.HasValue)
            return date.Value.ToString("yyyy-MM-dd");
        return Convert.ToString(raw, DeAt)?.Trim() ?? "";
    }

    private static string CategoryLabel(object? raw, DateTime? date, string? bucket)
    {
        if (date.HasValue && string.Equals(bucket, "month", StringComparison.OrdinalIgnoreCase))
            return date.Value.ToString("MM/yy", DeAt);
        if (date.HasValue)
            return date.Value.ToString("dd.MM.yy", DeAt);
        return Convert.ToString(raw, DeAt)?.Trim() ?? "";
    }

    private sealed class GroupAccumulator(string key, string label, DateTime? date)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public DateTime? Date { get; } = date;
        public Dictionary<string, Accumulator> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string name, decimal value)
        {
            var current = Values.GetValueOrDefault(name);
            Values[name] = new Accumulator(current.Sum + value, current.Count + 1);
        }
    }

    private sealed class BusinessAreaOpenValue(string colorKey)
    {
        public string ColorKey { get; set; } = colorKey;
        public decimal OpenOrders { get; set; }
        public decimal Framework { get; set; }
    }

    private sealed class DynamicGroup(string label, string colorKey, string color)
    {
        public string Label { get; } = label;
        public string ColorKey { get; } = colorKey;
        public string Color { get; } = color;
        public Dictionary<string, decimal> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct Accumulator(decimal Sum, int Count);
    private sealed record GroupPoint(string Key, string Label, DateTime? Date, Dictionary<string, decimal> Values);
}
