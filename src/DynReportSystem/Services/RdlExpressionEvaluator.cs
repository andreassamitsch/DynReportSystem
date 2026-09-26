using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

/// <summary>
/// Small, deliberately bounded evaluator for the display expressions actually
/// used throughout the imported Fuchshofer RDL estate. It is not a VB runtime.
/// Unsupported expressions fall back to the first referenced field instead of
/// inventing data.
/// </summary>
public sealed class RdlExpressionEvaluator
{
    private static readonly CultureInfo DeAt = CultureInfo.GetCultureInfo("de-AT");

    public object? ApplyFormat(object? value, string? format)
    {
        if (string.IsNullOrWhiteSpace(format) || value is null)
            return value;

        return Format(value, format);
    }

    public object? Evaluate(
        string expression,
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<Dictionary<string, object?>> scopeRows,
        IReadOnlyDictionary<string, DynamicParameterValue>? parameters = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var text = expression.Trim();
        if (!text.StartsWith('='))
            return text;

        text = text[1..].Trim();

        try
        {
            return Eval(text, row, scopeRows, parameters);
        }
        catch
        {
            var field = FirstField(text);
            return field is null ? text : QueryResult.Get(row, field);
        }
    }

    private object? Eval(
        string text,
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<Dictionary<string, object?>> scopeRows,
        IReadOnlyDictionary<string, DynamicParameterValue>? parameters)
    {
        text = StripOuterParens(text.Trim());

        if (TryStringLiteral(text, out var literal))
            return literal;

        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            return number;

        if (text.Equals("Nothing", StringComparison.OrdinalIgnoreCase))
            return null;

        if (text.Equals("True", StringComparison.OrdinalIgnoreCase))
            return true;

        if (text.Equals("False", StringComparison.OrdinalIgnoreCase))
            return false;

        var fieldMatch = Regex.Match(text, @"^Fields!(?<field>[^.]+).Value$", RegexOptions.IgnoreCase);
        if (fieldMatch.Success)
            return QueryResult.Get(row, fieldMatch.Groups["field"].Value);

        var parameterMatch = Regex.Match(
            text,
            @"^Parameters!(?<name>[^.]+).Value(?:((?<index>d+)))?$",
            RegexOptions.IgnoreCase);

        if (parameterMatch.Success)
            return ParameterValue(parameterMatch, parameters);

        var aggregate = Regex.Match(
            text,
            @"^(?<fn>Sum|Avg|Average|Count|CountDistinct|Min|Max|First|Last)s*(s*Fields!(?<field>[^.]+).Value(?:s*,.*)?)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (aggregate.Success)
            return Aggregate(
                aggregate.Groups["fn"].Value,
                aggregate.Groups["field"].Value,
                scopeRows);

        if (TryFunction(text, "IIf", out var iifArgs) && iifArgs.Count >= 3)
            return EvalCondition(iifArgs[0], row, scopeRows, parameters)
                ? Eval(iifArgs[1], row, scopeRows, parameters)
                : Eval(iifArgs[2], row, scopeRows, parameters);

        if (TryFunction(text, "Format", out var formatArgs) && formatArgs.Count >= 2)
        {
            var value = Eval(formatArgs[0], row, scopeRows, parameters);
            var format = Convert.ToString(Eval(formatArgs[1], row, scopeRows, parameters), DeAt) ?? "";
            return Format(value, format);
        }

        if (TryFunction(text, "Left", out var leftArgs) && leftArgs.Count >= 2)
        {
            var value = Convert.ToString(Eval(leftArgs[0], row, scopeRows, parameters), DeAt) ?? "";
            var count = ToInt(Eval(leftArgs[1], row, scopeRows, parameters));
            return value[..Math.Clamp(count, 0, value.Length)];
        }

        if (TryFunction(text, "Right", out var rightArgs) && rightArgs.Count >= 2)
        {
            var value = Convert.ToString(Eval(rightArgs[0], row, scopeRows, parameters), DeAt) ?? "";
            var count = Math.Clamp(ToInt(Eval(rightArgs[1], row, scopeRows, parameters)), 0, value.Length);
            return value[(value.Length - count)..];
        }

        if (TryFunction(text, "Trim", out var trimArgs) && trimArgs.Count >= 1)
            return Convert.ToString(Eval(trimArgs[0], row, scopeRows, parameters), DeAt)?.Trim() ?? "";

        if (TryFunction(text, "Len", out var lenArgs) && lenArgs.Count >= 1)
            return (Convert.ToString(Eval(lenArgs[0], row, scopeRows, parameters), DeAt) ?? "").Length;

        if (TryFunction(text, "IsNothing", out var nothingArgs) && nothingArgs.Count >= 1)
            return Eval(nothingArgs[0], row, scopeRows, parameters) is null;

        foreach (var conversion in new[] { "CStr", "CInt", "CLng", "CDec", "CDbl", "CDate" })
        {
            if (!TryFunction(text, conversion, out var args) || args.Count == 0)
                continue;

            var value = Eval(args[0], row, scopeRows, parameters);
            return conversion.ToLowerInvariant() switch
            {
                "cstr" => Convert.ToString(value, DeAt) ?? "",
                "cint" => Convert.ToInt32(value ?? 0, CultureInfo.InvariantCulture),
                "clng" => Convert.ToInt64(value ?? 0, CultureInfo.InvariantCulture),
                "cdec" => Convert.ToDecimal(value ?? 0, CultureInfo.InvariantCulture),
                "cdbl" => Convert.ToDouble(value ?? 0, CultureInfo.InvariantCulture),
                "cdate" => Convert.ToDateTime(value, DeAt),
                _ => value
            };
        }

        if (TryFunction(text, "DatePart", out var datePartArgs) && datePartArgs.Count >= 2)
        {
            var date = ToDate(Eval(datePartArgs[1], row, scopeRows, parameters));
            var interval = datePartArgs[0];

            if (interval.Contains("WeekOfYear", StringComparison.OrdinalIgnoreCase))
                return ISOWeek.GetWeekOfYear(date);

            if (interval.Contains("WeekDay", StringComparison.OrdinalIgnoreCase))
                return ((int)date.DayOfWeek) + 1;

            if (interval.Contains("Month", StringComparison.OrdinalIgnoreCase))
                return date.Month;

            if (interval.Contains("Year", StringComparison.OrdinalIgnoreCase))
                return date.Year;

            if (interval.Contains("Day", StringComparison.OrdinalIgnoreCase))
                return date.Day;
        }

        if (TryFunction(text, "DateAdd", out var dateAddArgs) && dateAddArgs.Count >= 3)
        {
            var unit = Convert.ToString(Eval(dateAddArgs[0], row, scopeRows, parameters), DeAt) ?? "";
            var amount = ToInt(Eval(dateAddArgs[1], row, scopeRows, parameters));
            var date = ToDate(Eval(dateAddArgs[2], row, scopeRows, parameters));

            return unit.ToLowerInvariant() switch
            {
                "d" or "day" => date.AddDays(amount),
                "ww" or "week" => date.AddDays(amount * 7d),
                "m" or "month" => date.AddMonths(amount),
                "yyyy" or "year" => date.AddYears(amount),
                _ => date
            };
        }

        if (text.Equals("Today()", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Today", StringComparison.OrdinalIgnoreCase))
            return DateTime.Today;

        if (text.Equals("Now()", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Now", StringComparison.OrdinalIgnoreCase))
            return DateTime.Now;

        var floor = Regex.Match(text, @"^System.Math.Floor((?<inner>.*))$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (floor.Success)
            return Math.Floor(ToDouble(Eval(floor.Groups["inner"].Value, row, scopeRows, parameters)));

        var concat = SplitTopLevel(text, '&');
        if (concat.Count > 1)
        {
            var sb = new StringBuilder();
            foreach (var part in concat)
                sb.Append(Convert.ToString(Eval(part, row, scopeRows, parameters), DeAt));
            return sb.ToString();
        }

        // Addition is used both numerically and as string concatenation in old
        // SSRS/VB reports. If every operand is numeric, add; otherwise concatenate.
        var plus = SplitTopLevel(text, '+');
        if (plus.Count > 1)
        {
            var values = plus.Select(part => Eval(part, row, scopeRows, parameters)).ToArray();
            if (values.All(IsNumeric))
                return values.Sum(ToDouble);

            return string.Concat(values.Select(value => Convert.ToString(value, DeAt)));
        }

        foreach (var op in new[] { "/", "*", "-" })
        {
            var parts = SplitTopLevel(text, op[0]);
            if (parts.Count <= 1)
                continue;

            var numbers = parts.Select(part => ToDouble(Eval(part, row, scopeRows, parameters))).ToArray();
            return op switch
            {
                "/" => numbers.Skip(1).Aggregate(numbers[0], (current, next) => next == 0 ? current : current / next),
                "*" => numbers.Aggregate(1d, (current, next) => current * next),
                "-" => numbers.Skip(1).Aggregate(numbers[0], (current, next) => current - next),
                _ => numbers[0]
            };
        }

        var firstField = FirstField(text);
        return firstField is null ? text : QueryResult.Get(row, firstField);
    }

    private bool EvalCondition(
        string expression,
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<Dictionary<string, object?>> scopeRows,
        IReadOnlyDictionary<string, DynamicParameterValue>? parameters)
    {
        var text = StripOuterParens(expression.Trim());

        if (text.StartsWith("Not ", StringComparison.OrdinalIgnoreCase))
            return !ToBool(EvalCondition(text[4..], row, scopeRows, parameters));

        foreach (var logical in new[] { " AndAlso ", " And ", " OrElse ", " Or " })
        {
            var parts = SplitTopLevel(text, logical);
            if (parts.Count <= 1)
                continue;

            return logical.Contains("And", StringComparison.OrdinalIgnoreCase)
                ? parts.All(part => EvalCondition(part, row, scopeRows, parameters))
                : parts.Any(part => EvalCondition(part, row, scopeRows, parameters));
        }

        if (TryFunction(text, "IsNothing", out var nothingArgs) && nothingArgs.Count > 0)
            return Eval(nothingArgs[0], row, scopeRows, parameters) is null;

        foreach (var op in new[] { "<>", ">=", "<=", "=", ">", "<" })
        {
            var parts = SplitTopLevel(text, op);
            if (parts.Count != 2)
                continue;

            var left = Eval(parts[0], row, scopeRows, parameters);
            var right = Eval(parts[1], row, scopeRows, parameters);
            var compare = Compare(left, right);

            return op switch
            {
                "=" => compare == 0,
                "<>" => compare != 0,
                ">" => compare > 0,
                "<" => compare < 0,
                ">=" => compare >= 0,
                "<=" => compare <= 0,
                _ => false
            };
        }

        return ToBool(Eval(text, row, scopeRows, parameters));
    }

    private static object? ParameterValue(
        Match match,
        IReadOnlyDictionary<string, DynamicParameterValue>? parameters)
    {
        if (parameters is null
            || !parameters.TryGetValue(match.Groups["name"].Value, out var state))
            return null;

        if (match.Groups["index"].Success
            && int.TryParse(match.Groups["index"].Value, out var index))
            return index >= 0 && index < state.Values.Count
                ? state.Values[index]
                : null;

        return state.MultiValue ? state.Values.ToArray() : state.Values.FirstOrDefault();
    }

    private static object? Aggregate(
        string function,
        string field,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var values = rows.Select(row => QueryResult.Get(row, field)).Where(x => x is not null).ToArray();

        if (function.Equals("Count", StringComparison.OrdinalIgnoreCase))
            return values.Length;

        if (function.Equals("CountDistinct", StringComparison.OrdinalIgnoreCase))
            return values.Select(x => Convert.ToString(x, DeAt) ?? "")
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Count();

        if (function.Equals("First", StringComparison.OrdinalIgnoreCase))
            return values.FirstOrDefault();

        if (function.Equals("Last", StringComparison.OrdinalIgnoreCase))
            return values.LastOrDefault();

        var numeric = values.Where(IsNumeric).Select(ToDouble).ToArray();
        if (numeric.Length == 0)
            return null;

        return function.ToLowerInvariant() switch
        {
            "avg" or "average" => numeric.Average(),
            "min" => numeric.Min(),
            "max" => numeric.Max(),
            _ => numeric.Sum()
        };
    }

    private static string Format(object? value, string format)
    {
        if (value is null)
            return "";

        try
        {
            if (value is DateTime date)
                return date.ToString(format, DeAt);

            if (value is DateTimeOffset dto)
                return dto.ToString(format, DeAt);

            if (value is IFormattable formattable)
                return formattable.ToString(format, DeAt);
        }
        catch { }

        return Convert.ToString(value, DeAt) ?? "";
    }

    private static string? FirstField(string text)
    {
        var match = Regex.Match(text, @"Fields!(?<field>[^.]+).Value", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["field"].Value : null;
    }

    private static bool TryFunction(string text, string name, out List<string> args)
    {
        args = [];
        var prefix = name + "(";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !text.EndsWith(')'))
            return false;

        args = SplitArguments(text[prefix.Length..^1]);
        return true;
    }

    private static List<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch == '"')
            {
                quoted = !quoted;
                current.Append(ch);
                continue;
            }

            if (!quoted)
            {
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                else if (ch == ',' && depth == 0)
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }
            }

            current.Append(ch);
        }

        result.Add(current.ToString().Trim());
        return result;
    }

    private static List<string> SplitTopLevel(string text, char separator) =>
        SplitTopLevel(text, separator.ToString());

    private static List<string> SplitTopLevel(string text, string separator)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch == '"')
            {
                quoted = !quoted;
                current.Append(ch);
                continue;
            }

            if (!quoted)
            {
                if (ch == '(') depth++;
                else if (ch == ')') depth--;

                if (depth == 0
                    && i + separator.Length <= text.Length
                    && text.AsSpan(i, separator.Length).Equals(separator, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                    i += separator.Length - 1;
                    continue;
                }
            }

            current.Append(ch);
        }

        result.Add(current.ToString().Trim());
        return result;
    }

    private static string StripOuterParens(string text)
    {
        while (text.Length > 1 && text[0] == '(' && text[^1] == ')' && BalancedOuterParens(text))
            text = text[1..^1].Trim();

        return text;
    }

    private static bool BalancedOuterParens(string text)
    {
        var depth = 0;
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"') quoted = !quoted;
            if (quoted) continue;

            if (ch == '(') depth++;
            else if (ch == ')') depth--;

            if (depth == 0 && i < text.Length - 1)
                return false;
        }

        return depth == 0;
    }

    private static bool TryStringLiteral(string text, out string value)
    {
        value = "";
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
            return false;

        value = text[1..^1].Replace("\"\"", "\"");
        return true;
    }

    private static bool IsNumeric(object? value)
    {
        if (value is null)
            return false;

        return value is byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal
            || double.TryParse(Convert.ToString(value, DeAt), NumberStyles.Any, DeAt, out _);
    }

    private static double ToDouble(object? value)
    {
        if (value is null)
            return 0;

        try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch
        {
            return double.TryParse(Convert.ToString(value, DeAt), NumberStyles.Any, DeAt, out var parsed)
                ? parsed
                : 0;
        }
    }

    private static int ToInt(object? value) => (int)Math.Round(ToDouble(value));

    private static bool ToBool(object? value)
    {
        if (value is bool boolean)
            return boolean;

        var text = Convert.ToString(value, DeAt)?.Trim() ?? "";
        return bool.TryParse(text, out var parsed)
            ? parsed
            : text is "1" or "J" or "Y" or "T";
    }

    private static DateTime ToDate(object? value)
    {
        if (value is DateTime date)
            return date;

        if (value is DateTimeOffset offset)
            return offset.DateTime;

        return DateTime.TryParse(Convert.ToString(value, DeAt), DeAt, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed
            : DateTime.MinValue;
    }

    private static int Compare(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;

        if (IsNumeric(left) && IsNumeric(right))
            return ToDouble(left).CompareTo(ToDouble(right));

        if (left is DateTime or DateTimeOffset || right is DateTime or DateTimeOffset)
            return ToDate(left).CompareTo(ToDate(right));

        return string.Compare(
            Convert.ToString(left, DeAt),
            Convert.ToString(right, DeAt),
            StringComparison.CurrentCultureIgnoreCase);
    }
}
