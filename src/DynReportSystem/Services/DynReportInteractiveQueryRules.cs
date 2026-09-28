using System.Text.RegularExpressions;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

public static class DynReportInteractiveQueryRules
{

    public static bool ShouldUseServerMode(
        LoadedDynReportPackage package,
        DynVisual visual)
    {
        if (!visual.Type.Equals("table", StringComparison.OrdinalIgnoreCase)
            || visual.Table is null)
            return false;

        if (string.Equals(visual.Table.DataMode, "Server", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(visual.Table.DataMode, "Auto", StringComparison.OrdinalIgnoreCase))
            return false;

        var dataSet = package.Document.Datasets.FirstOrDefault(x =>
            x.Id.Equals(visual.Dataset, StringComparison.OrdinalIgnoreCase));

        if (dataSet is null
            || !package.TextFiles.TryGetValue(dataSet.QueryFile, out var sql)
            || string.IsNullOrWhiteSpace(sql)
            || !TryNormalizeComposableSelect(
                dataSet,
                sql,
                out _,
                out _))
            return false;

        var presentation = visual.Table.PresentationMode ?? "Rows";
        if (!presentation.Equals("Rows", StringComparison.OrdinalIgnoreCase))
            return true;

        var consumers = package.Document.Pages
            .SelectMany(page => page.Components)
            .Where(component =>
                component.Dataset.Equals(visual.Dataset, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return consumers.Length > 0
            && consumers.All(component =>
                component.Type.Equals("table", StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeComposableSelect(
        DynReportDataset dataSet,
        string sql)
    {
        if (!TryNormalizeComposableSelect(dataSet, sql, out var normalized, out var error))
            throw new NotSupportedException(error);

        return normalized;
    }

    public static bool TryNormalizeComposableSelect(
        DynReportDataset dataSet,
        string sql,
        out string normalized,
        out string error)
    {
        normalized = (sql ?? "").Trim().TrimStart('\uFEFF');
        error = "";

        if (!dataSet.CommandType.Equals("Text", StringComparison.OrdinalIgnoreCase))
        {
            error =
                $"Dataset '{dataSet.Id}' muss für serverseitiges Paging SQL-Text verwenden.";
            return false;
        }

        if (normalized.EndsWith(';'))
            normalized = normalized[..^1].TrimEnd();

        var inspection = StripLeadingComments(normalized);

        if (!Regex.IsMatch(
                inspection,
                @"^SELECT\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            error =
                $"Dataset '{dataSet.Id}' ist nicht als komponierbare SELECT-Abfrage aufgebaut. " +
                "Für DataMode=Server muss die SQL-Datei direkt mit SELECT beginnen.";
            return false;
        }

        if (inspection.Contains(';'))
        {
            error =
                $"Dataset '{dataSet.Id}' enthält mehrere SQL-Anweisungen und kann nicht serverseitig komponiert werden.";
            return false;
        }

        if (Regex.IsMatch(
                inspection,
                @"\bORDER\s+BY\b|\bFOR\s+(XML|JSON)\b|\bOPTION\s*\(",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            error =
                $"Dataset '{dataSet.Id}' enthält ORDER BY/FOR/OPTION. " +
                "Sortierung muss bei DataMode=Server über die semantische Tabellendefinition erfolgen.";
            return false;
        }

        return true;
    }

    private static string StripLeadingComments(string sql)
    {
        var value = sql;

        while (true)
        {
            var trimmed = value.TrimStart();

            if (trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                var lineEnd = trimmed.IndexOfAny(['\r', '\n']);
                value = lineEnd < 0 ? "" : trimmed[(lineEnd + 1)..];
                continue;
            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                var end = trimmed.IndexOf("*/", 2, StringComparison.Ordinal);
                value = end < 0 ? "" : trimmed[(end + 2)..];
                continue;
            }

            return trimmed;
        }
    }
}
