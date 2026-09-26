using System.Globalization;
using System.Text.Json;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

public sealed class DynExpressionEngine
{
    private static readonly CultureInfo DeAt = CultureInfo.GetCultureInfo("de-AT");

    public object? Evaluate(
        DynExpr? expression,
        DynReportDocument document,
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyDictionary<string, DynReportParameterValue>? parameters = null)
    {
        if (expression is null)
            return null;

        return EvaluateInternal(
            expression,
            new EvalContext(document, rows, parameters),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    public bool EvaluateBool(
        DynExpr? expression,
        DynReportDocument document,
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyDictionary<string, DynReportParameterValue>? parameters = null) =>
        ToBool(Evaluate(expression, document, rows, parameters));

    public string FormatValue(object? value, string? format, string? unit = null)
    {
        if (value is null)
            return "–";

        var text = value switch
        {
            DateTime dt => string.IsNullOrWhiteSpace(format)
                ? dt.ToString("dd.MM.yyyy HH:mm", DeAt)
                : dt.ToString(format, DeAt),
            DateTimeOffset dto => string.IsNullOrWhiteSpace(format)
                ? dto.ToString("dd.MM.yyyy HH:mm", DeAt)
                : dto.ToString(format, DeAt),
            IFormattable formattable when !string.IsNullOrWhiteSpace(format) =>
                formattable.ToString(format, DeAt),
            IFormattable formattable => formattable.ToString(null, DeAt),
            _ => Convert.ToString(value, DeAt) ?? ""
        };

        return string.IsNullOrWhiteSpace(unit) ? text : $"{text} {unit}";
    }

    private object? EvaluateInternal(
        DynExpr expression,
        EvalContext context,
        HashSet<string> stack)
    {
        var op = expression.Op.Trim().ToLowerInvariant();

        return op switch
        {
            "const" => Constant(expression.Value),
            "parameter" => Parameter(expression.Parameter, context.Parameters),
            "ref" => Reference(expression.Name, context, stack),
            "field" => FirstField(expression.Field, FilterRows(context, expression.Where, stack)),
            "first" => FirstField(expression.Field, FilterRows(context, expression.Where, stack)),
            "last" => LastField(expression.Field, FilterRows(context, expression.Where, stack)),
            "sum" => SumField(expression.Field, FilterRows(context, expression.Where, stack)),
            "avg" or "average" => AverageField(expression.Field, FilterRows(context, expression.Where, stack)),
            "min" => MinField(expression.Field, FilterRows(context, expression.Where, stack)),
            "max" => MaxField(expression.Field, FilterRows(context, expression.Where, stack)),
            "count" => FilterRows(context, expression.Where, stack).Count,
            "distinctcount" => DistinctCount(expression.Field, FilterRows(context, expression.Where, stack)),
            "distinctjoin" => DistinctJoin(expression, context, stack),
            "sumdistinct" => SumDistinct(expression, context, stack),
            "latest" => Latest(expression, context, stack),
            "countgroups" => CountGroups(expression, context, stack),
            "sumgroups" => SumGroups(expression, context, stack),
            "add" => NumericArgs(expression, context, stack).Sum(),
            "subtract" => Subtract(expression, context, stack),
            "multiply" => NumericArgs(expression, context, stack).Aggregate(1m, (a, b) => a * b),
            "divide" => Divide(expression, context, stack),
            "maxof" => NumericArgs(expression, context, stack).DefaultIfEmpty(0m).Max(),
            "minof" => NumericArgs(expression, context, stack).DefaultIfEmpty(0m).Min(),
            "round" => Round(expression, context, stack),
            "coalesce" => Coalesce(expression, context, stack),
            "if" => If(expression, context, stack),
            "eq" => CompareArgs(expression, context, stack) == 0,
            "ne" => CompareArgs(expression, context, stack) != 0,
            "gt" => CompareArgs(expression, context, stack) > 0,
            "gte" => CompareArgs(expression, context, stack) >= 0,
            "lt" => CompareArgs(expression, context, stack) < 0,
            "lte" => CompareArgs(expression, context, stack) <= 0,
            "and" => expression.Args.All(x => ToBool(EvaluateInternal(x, context, stack))),
            "or" => expression.Args.Any(x => ToBool(EvaluateInternal(x, context, stack))),
            "not" => !ToBool(expression.Args.Count > 0
                ? EvaluateInternal(expression.Args[0], context, stack)
                : null),
            "isempty" => IsEmpty(expression.Args.Count > 0
                ? EvaluateInternal(expression.Args[0], context, stack)
                : null),
            "hasvalue" => !IsEmpty(expression.Args.Count > 0
                ? EvaluateInternal(expression.Args[0], context, stack)
                : null),
            "contains" => Contains(expression, context, stack),
            "now" => DateTime.Now,
            "today" => DateTime.Today,
            "hour" => Hour(expression, context, stack),
            _ => null
        };
    }

    private object? Reference(string name, EvalContext context, HashSet<string> stack)
    {
        if (string.IsNullOrWhiteSpace(name)
            || !context.Document.Calculations.TryGetValue(name, out var expression))
            return null;

        if (!stack.Add(name))
            throw new InvalidDataException($"Zirkuläre Berechnung '{name}'.");

        try
        {
            return EvaluateInternal(expression, context, stack);
        }
        finally
        {
            stack.Remove(name);
        }
    }

    private List<Dictionary<string, object?>> FilterRows(
        EvalContext context,
        DynExpr? filter,
        HashSet<string> stack)
    {
        if (filter is null)
            return context.Rows.ToList();

        return context.Rows
            .Where(row => ToBool(EvaluateInternal(
                filter,
                context with { Rows = [row] },
                stack)))
            .ToList();
    }

    private object? DistinctJoin(DynExpr expression, EvalContext context, HashSet<string> stack)
    {
        var rows = FilterRows(context, expression.Where, stack);
        var values = rows
            .Select(row => TextValue(QueryResult.Get(row, expression.Field)))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return string.Join(
            string.IsNullOrWhiteSpace(expression.Separator) ? ", " : expression.Separator,
            values);
    }

    private object SumDistinct(DynExpr expression, EvalContext context, HashSet<string> stack)
    {
        var rows = FilterRows(context, expression.Where, stack);

        if (expression.DistinctBy.Count == 0)
            return SumField(expression.Field, rows);

        return rows
            .GroupBy(row => string.Join(
                "",
                expression.DistinctBy.Select(field =>
                    TextValue(QueryResult.Get(row, field)))),
                StringComparer.Ordinal)
            .Sum(group => AverageField(expression.Field, group.ToList()));
    }

    private object? Latest(DynExpr expression, EvalContext context, HashSet<string> stack)
    {
        var rows = FilterRows(context, expression.Where, stack);
        if (rows.Count == 0)
            return null;

        IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;

        foreach (var sort in expression.OrderBy)
        {
            Func<Dictionary<string, object?>, object?> key = row =>
                QueryResult.Get(row, sort.Field);

            var desc = sort.Direction.Equals("Desc", StringComparison.OrdinalIgnoreCase);

            if (ordered is null)
            {
                ordered = desc
                    ? rows.OrderByDescending(key, DynObjectComparer.Instance)
                    : rows.OrderBy(key, DynObjectComparer.Instance);
            }
            else
            {
                ordered = desc
                    ? ordered.ThenByDescending(key, DynObjectComparer.Instance)
                    : ordered.ThenBy(key, DynObjectComparer.Instance);
            }
        }

        var selected = ordered?.FirstOrDefault() ?? rows[0];
        return QueryResult.Get(selected, expression.Field);
    }

    private object CountGroups(DynExpr expression, EvalContext context, HashSet<string> stack)
    {
        var groups = Groups(expression, context);
        if (expression.Where is null)
            return groups.Count;

        return groups.Count(group =>
            ToBool(EvaluateInternal(
                expression.Where,
                context with { Rows = group },
                stack)));
    }

    private object SumGroups(DynExpr expression, EvalContext context, HashSet<string> stack)
    {
        if (expression.Args.Count == 0)
            return 0m;

        return Groups(expression, context)
            .Sum(group => ToDecimal(EvaluateInternal(
                expression.Args[0],
                context with { Rows = group },
                stack)) ?? 0m);
    }

    private static List<List<Dictionary<string, object?>>> Groups(
        DynExpr expression,
        EvalContext context)
    {
        if (expression.GroupBy.Count == 0)
            return [context.Rows.ToList()];

        return context.Rows
            .GroupBy(row => string.Join(
                "",
                expression.GroupBy.Select(field =>
                    TextValue(QueryResult.Get(row, field)))),
                StringComparer.Ordinal)
            .Select(group => group.ToList())
            .ToList();
    }

    private decimal[] NumericArgs(DynExpr expression, EvalContext context, HashSet<string> stack) =>
        expression.Args
            .Select(x => ToDecimal(EvaluateInternal(x, context, stack)) ?? 0m)
            .ToArray();

    private object Subtract(DynExpr expression, EvalContext context, HashSet<string> stack)
    {
        var values = NumericArgs(expression, context, stack);
        if (values.Length == 0)
            return 0m;

        return values.Skip(1).Aggregate(values[0], (current, next) => current - next);
    }

    private object Divide(DynExpr expression, EvalContext context, HashSet<string> stack)
    {
        var values = NumericArgs(expression, context, stack);
        if (values.Length == 0)
            return 0m;

        return values.Skip(1).Aggregate(values[0], (current, next) =>
            next == 0m ? 0m : current / next);
    }

    private object Round(DynExpr expression, EvalContext context, HashSet<string> stack)
    {
        if (expression.Args.Count == 0)
            return 0m;

        var value = ToDecimal(EvaluateInternal(expression.Args[0], context, stack)) ?? 0m;
        var decimals = expression.Args.Count > 1
            ? Convert.ToInt32(ToDecimal(EvaluateInternal(expression.Args[1], context, stack)) ?? 0m)
            : 0;

        return Math.Round(value, Math.Clamp(decimals, 0, 8));
    }

    private object? Coalesce(DynExpr expression, EvalContext context, HashSet<string> stack)
    {
        foreach (var arg in expression.Args)
        {
            var value = EvaluateInternal(arg, context, stack);
            if (!IsEmpty(value))
                return value;
        }

        return null;
    }

    private object? If(DynExpr expression, EvalContext context, HashSet<string> stack)
    {
        if (expression.Args.Count < 2)
            return null;

        var condition = ToBool(EvaluateInternal(expression.Args[0], context, stack));
        if (condition)
            return EvaluateInternal(expression.Args[1], context, stack);

        return expression.Args.Count >= 3
            ? EvaluateInternal(expression.Args[2], context, stack)
            : null;
    }

    private int CompareArgs(DynExpr expression, EvalContext context, HashSet<string> stack)
    {
        if (expression.Args.Count < 2)
            return 0;

        return Compare(
            EvaluateInternal(expression.Args[0], context, stack),
            EvaluateInternal(expression.Args[1], context, stack));
    }

    private bool Contains(DynExpr expression, EvalContext context, HashSet<string> stack)
    {
        if (expression.Args.Count < 2)
            return false;

        var haystack = TextValue(EvaluateInternal(expression.Args[0], context, stack));
        var needle = TextValue(EvaluateInternal(expression.Args[1], context, stack));

        return haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
    }

    private static object? Parameter(
        string name,
        IReadOnlyDictionary<string, DynReportParameterValue>? parameters)
    {
        if (parameters is null
            || !parameters.TryGetValue(name, out var state)
            || state.Values.Count == 0)
            return null;

        return state.MultiValue
            ? state.Values.ToArray()
            : state.Values[0];
    }

    private static object? Constant(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined
            || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.TryGetDateTime(out var date)
                ? date
                : value.GetString(),
            JsonValueKind.Number => value.TryGetDecimal(out var number)
                ? number
                : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => value.EnumerateArray().Select(Constant).ToArray(),
            _ => value.ToString()
        };
    }

    private static object? FirstField(
        string field,
        IReadOnlyList<Dictionary<string, object?>> rows) =>
        rows.Select(row => QueryResult.Get(row, field))
            .FirstOrDefault(value => value is not null);

    private static object? LastField(
        string field,
        IReadOnlyList<Dictionary<string, object?>> rows) =>
        rows.Select(row => QueryResult.Get(row, field))
            .LastOrDefault(value => value is not null);

    private static decimal SumField(
        string field,
        IReadOnlyList<Dictionary<string, object?>> rows) =>
        rows.Sum(row => ToDecimal(QueryResult.Get(row, field)) ?? 0m);

    private static decimal AverageField(
        string field,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var values = rows
            .Select(row => ToDecimal(QueryResult.Get(row, field)))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToArray();

        return values.Length == 0 ? 0m : values.Average();
    }

    private static object? MinField(
        string field,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var values = rows.Select(row => QueryResult.Get(row, field))
            .Where(x => x is not null)
            .ToArray();

        return values.Length == 0
            ? null
            : values.Min(DynObjectComparer.Instance);
    }

    private static object? MaxField(
        string field,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var values = rows.Select(row => QueryResult.Get(row, field))
            .Where(x => x is not null)
            .ToArray();

        return values.Length == 0
            ? null
            : values.Max(DynObjectComparer.Instance);
    }

    private static int DistinctCount(
        string field,
        IReadOnlyList<Dictionary<string, object?>> rows) =>
        rows.Select(row => TextValue(QueryResult.Get(row, field)))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Count();

    private static bool IsEmpty(object? value) =>
        value is null
        || value is string text && string.IsNullOrWhiteSpace(text)
        || value is Array array && array.Length == 0;

    private static bool ToBool(object? value)
    {
        if (value is bool flag)
            return flag;

        var number = ToDecimal(value);
        if (number.HasValue)
            return number.Value != 0m;

        var text = TextValue(value);
        return text.Equals("true", StringComparison.OrdinalIgnoreCase)
               || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || text.Equals("ja", StringComparison.OrdinalIgnoreCase)
               || text.Equals("y", StringComparison.OrdinalIgnoreCase)
               || text.Equals("j", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? ToDecimal(object? value)
    {
        if (value is null)
            return null;

        try
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return decimal.TryParse(
                Convert.ToString(value, DeAt),
                NumberStyles.Any,
                DeAt,
                out var parsed)
                ? parsed
                : null;
        }
    }

    private static int Compare(object? left, object? right) =>
        DynObjectComparer.Instance.Compare(left, right);

    private static string TextValue(object? value) =>
        value switch
        {
            null => "",
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, DeAt)?.Trim() ?? ""
        };

    private object Hour(DynExpr expression, EvalContext context, HashSet<string> stack)
    {
        var value = expression.Args.Count > 0
            ? EvaluateInternal(expression.Args[0], context, stack)
            : DateTime.Now;

        if (value is DateTime date)
            return date.Hour;

        if (value is DateTimeOffset offset)
            return offset.Hour;

        return DateTime.TryParse(
            Convert.ToString(value, DeAt),
            DeAt,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed.Hour
            : 0;
    }

    private sealed record EvalContext(
        DynReportDocument Document,
        IReadOnlyList<Dictionary<string, object?>> Rows,
        IReadOnlyDictionary<string, DynReportParameterValue>? Parameters);

    private sealed class DynObjectComparer : IComparer<object?>
    {
        public static DynObjectComparer Instance { get; } = new();

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var nx = ToDecimal(x);
            var ny = ToDecimal(y);
            if (nx.HasValue && ny.HasValue)
                return nx.Value.CompareTo(ny.Value);

            if (TryDate(x, out var dx) && TryDate(y, out var dy))
                return dx.CompareTo(dy);

            return string.Compare(
                Convert.ToString(x, DeAt),
                Convert.ToString(y, DeAt),
                StringComparison.CurrentCultureIgnoreCase);
        }

        private static bool TryDate(object value, out DateTime date)
        {
            if (value is DateTime direct)
            {
                date = direct;
                return true;
            }

            if (value is DateTimeOffset offset)
            {
                date = offset.DateTime;
                return true;
            }

            return DateTime.TryParse(
                Convert.ToString(value, DeAt),
                DeAt,
                DateTimeStyles.AllowWhiteSpaces,
                out date);
        }
    }
}
