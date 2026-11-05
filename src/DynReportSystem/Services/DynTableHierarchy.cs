using System.Globalization;
using System.Text.Json;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

/// <summary>
/// Creates stable, multi-level groups from a report dataset. Keeps whole groups
/// together when the UI pages by root group (e.g. by sales order).
/// </summary>
public static class DynTableHierarchy
{
    public static IReadOnlyList<DynHierarchyNode> Build(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<DynHierarchyLevel> levels)
    {
        if (levels.Count == 0)
            return [];

        return BuildLevel(rows, levels, 0, "");
    }

    private static List<DynHierarchyNode> BuildLevel(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<DynHierarchyLevel> levels,
        int depth,
        string parentPath)
    {
        var level = levels[depth];
        var result = new List<DynHierarchyNode>();
        foreach (var grouping in rows.GroupBy(
                     row => Value(QueryResult.Get(row, level.Field)),
                     StringComparer.OrdinalIgnoreCase))
        {
            var groupRows = grouping.ToArray();
            // JSON avoids collisions between e.g. order/position "1/23" and "12/3".
            var path = JsonSerializer.Serialize(new[] { parentPath, level.Field, grouping.Key });
            var node = new DynHierarchyNode(path, grouping.Key, depth, groupRows);

            if (depth + 1 < levels.Count)
                node.Children.AddRange(BuildLevel(groupRows, levels, depth + 1, path));

            result.Add(node);
        }
        return result;
    }

    public static int? MaxOpenCount(DynHierarchyNode node, string field)
    {
        if (string.IsNullOrWhiteSpace(field))
            return null;

        var values = node.Rows
            .Select(row => QueryResult.Get(row, field))
            .Where(value => value is not null && value is not DBNull)
            .Select(value => decimal.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var number) ? (decimal?)number : null)
            .Where(value => value.HasValue)
            .ToArray();

        return values.Length == 0
            ? null
            : (int)Math.Clamp(values.Max()!.Value, 0, int.MaxValue);
    }

    public static DateTime? LatestCompleted(DynHierarchyNode node, string field)
    {
        if (string.IsNullOrWhiteSpace(field))
            return null;

        var dates = node.Rows
            .Select(row => QueryResult.Get(row, field))
            .Select(value => value switch
            {
                DateTime date => (DateTime?)date,
                DateTimeOffset offset => offset.DateTime,
                string text when DateTime.TryParse(
                    text, CultureInfo.GetCultureInfo("de-AT"),
                    DateTimeStyles.AllowWhiteSpaces,
                    out var parsed) => parsed,
                _ => null
            })
            .Where(date => date.HasValue)
            .ToArray();

        return dates.Length == 0 ? null : dates.Max();
    }

    public static IReadOnlyList<DynHierarchySummary> Summaries(
        DynHierarchyNode node,
        DynHierarchyLevel level)
    {
        if (string.IsNullOrWhiteSpace(level.SummaryField))
            return [];

        var rows = node.Rows.AsEnumerable();
        var openCount = MaxOpenCount(node, level.OpenCountField);

        if (level.SummaryOpenOnly
            && openCount is > 0
            && !string.IsNullOrWhiteSpace(level.SummaryOpenCountField))
        {
            rows = rows.Where(row =>
                ToDecimal(QueryResult.Get(row, level.SummaryOpenCountField)) > 0);
        }

        if (!string.IsNullOrWhiteSpace(level.SummarySortField))
        {
            rows = rows.OrderBy(
                row => ToDecimal(QueryResult.Get(row, level.SummarySortField))
                    ?? decimal.MaxValue);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var summaries = new List<DynHierarchySummary>();

        foreach (var row in rows)
        {
            var summary = Value(QueryResult.Get(row, level.SummaryField));
            if (string.IsNullOrWhiteSpace(summary))
                continue;

            var label = Value(QueryResult.Get(row, level.SummaryLabelField));
            var distinct = string.IsNullOrWhiteSpace(level.SummaryDistinctField)
                ? label + "\u001f" + summary
                : Value(QueryResult.Get(row, level.SummaryDistinctField));

            if (seen.Add(distinct))
                summaries.Add(new DynHierarchySummary(label, summary));
        }

        return summaries;
    }

    private static decimal? ToDecimal(object? value) =>
        value is null || value is DBNull
            ? null
            : decimal.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var parsed) ? parsed : null;

    public static string Value(object? value) => value switch
    {
        null or DBNull => "",
        DateTime date => date.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("de-AT")),
        DateTimeOffset date => date.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("de-AT")),
        _ => Convert.ToString(value, CultureInfo.GetCultureInfo("de-AT")) ?? ""
    };
}

public sealed class DynHierarchyNode(
    string path,
    string key,
    int depth,
    IReadOnlyList<Dictionary<string, object?>> rows)
{
    public string Path { get; } = path;
    public string Key { get; } = key;
    public int Depth { get; } = depth;
    public IReadOnlyList<Dictionary<string, object?>> Rows { get; } = rows;
    public List<DynHierarchyNode> Children { get; } = [];
}

public sealed record DynHierarchySummary(string Label, string Text);
