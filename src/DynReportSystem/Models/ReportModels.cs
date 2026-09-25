namespace DynReportSystem.Models;

public sealed record ReportFilters(DateTime Start, DateTime Ende, string Kunde, string Vertriebsmitarbeiter);
public sealed record QueryBinding(string SqlName, string ParameterName);
public sealed record RdlDataset(string Name, string Sql, IReadOnlyList<QueryBinding> Bindings, IReadOnlyList<string> Fields);

public sealed class QueryResult
{
    public required string Dataset { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<Dictionary<string, object?>> Rows { get; init; }
    public bool Truncated { get; init; }

    public decimal? Sum(string field)
    {
        if (Truncated || !Columns.Contains(field, StringComparer.OrdinalIgnoreCase))
            return null;

        decimal total = 0;
        foreach (var row in Rows)
        {
            var value = Get(row, field);
            if (value is null) continue;
            try { total += Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
        }
        return total;
    }

    public static object? Get(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var result) ? result : null;
}
