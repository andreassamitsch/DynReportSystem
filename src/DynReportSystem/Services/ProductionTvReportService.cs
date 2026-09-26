using System.Globalization;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

public sealed class ProductionTvReportService
{
    private static readonly CultureInfo DeAt = CultureInfo.GetCultureInfo("de-AT");

    public ProductionTvDashboard Build(QueryResult result, ProductionTvDefinition config)
    {
        if (result.Rows.Count == 0)
            return new ProductionTvDashboard();

        var operationGroups = result.Rows
            .GroupBy(row => new ProductionGroupKey(
                Text(row, config.DepartmentField),
                Text(row, config.MachineField),
                Text(row, config.OrderField),
                Text(row, config.ArticleField),
                Text(row, config.OperationNumberField)),
                ProductionGroupKeyComparer.Instance)
            .Select(group => BuildCard(group.ToArray(), config))
            .Where(card => !string.IsNullOrWhiteSpace(card.Machine))
            .OrderBy(card => card.Department, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(card => card.Machine, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(card => card.Order, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(card => card.OperationNumber, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var departments = operationGroups
            .GroupBy(card => string.IsNullOrWhiteSpace(card.Department) ? "Ohne Bereich" : card.Department,
                StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new ProductionTvDepartment
            {
                Name = group.Key,
                Cards = group.ToList()
            })
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var machines = operationGroups
            .Select(card => card.Machine)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Count();

        return new ProductionTvDashboard
        {
            Departments = departments,
            MachineCount = machines,
            ProductionCount = operationGroups.Count(card =>
                card.Status.Equals("Produktion", StringComparison.OrdinalIgnoreCase)),
            SetupCount = operationGroups.Count(card =>
                card.Status.Equals("Rüsten", StringComparison.OrdinalIgnoreCase)),
            WarningCount = operationGroups.Count(card => card.Warnings.Count > 0),
            UnplannedDowntimeHours = operationGroups.Sum(card => card.UnplannedDowntimeHours)
        };
    }

    private static ProductionTvCard BuildCard(
        IReadOnlyList<Dictionary<string, object?>> rows,
        ProductionTvDefinition config)
    {
        var latest = rows
            .OrderByDescending(row => Int(row, config.CurrentField))
            .ThenByDescending(row => Date(row, config.StatusBeginField) ?? DateTime.MinValue)
            .ThenByDescending(row => Date(row, config.StatusEndField) ?? DateTime.MinValue)
            .First();

        var status = Text(latest, config.StatusField);
        var statusColor = config.StatusColors.TryGetValue(status, out var color)
            ? color
            : config.FallbackStatusColor;

        var productionMinutes = rows
            .Where(row => Text(row, config.StatusField)
                .Equals("Produktion", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => new StatusPeriodKey(
                    Date(row, config.StatusBeginField),
                    Date(row, config.StatusEndField),
                    Text(row, config.StatusField)),
                StatusPeriodKeyComparer.Instance)
            .Sum(group => Average(group, config.DurationField) ?? 0m);

        var releasedSumAverage = Average(
            rows.Where(row => Decimal(row, config.ReleasedSumField) is > 0m),
            config.ReleasedSumField) ?? 0m;

        var shiftCycleActual = productionMinutes / (releasedSumAverage > 0 ? releasedSumAverage : 1m);
        var cycleTarget = FirstDecimal(rows, config.CycleTargetField);
        var cycleActual = FirstDecimal(rows, config.CycleActualField);
        var setupActual = FirstDecimal(rows, config.SetupActualField);
        var setupTarget = FirstDecimal(rows, config.SetupTargetField);

        // Matches the original SSRS expression Sum(Fields!RELEASEDCOUNT.Value)
        // in the operation scope. Keep this intentionally aligned with the
        // productive report instead of inventing a different quantity rule.
        var releasedQuantity = rows
            .Sum(row => Decimal(row, config.ReleasedField) ?? 0m);

        var shiftTargetQuantity = cycleTarget is > 0m
            ? productionMinutes / cycleTarget.Value
            : (decimal?)null;

        var lastContainerAt = rows
            .Select(row => Date(row, config.ContainerFinishedField))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .DefaultIfEmpty()
            .Max();

        DateTime? lastContainerDate = lastContainerAt == default
            ? null
            : lastContainerAt;

        Dictionary<string, object?>? lastContainerRow = null;
        if (lastContainerDate.HasValue)
        {
            lastContainerRow = rows
                .Where(row => Date(row, config.ContainerFinishedField) == lastContainerDate)
                .FirstOrDefault();
        }

        var containerElapsed = lastContainerDate.HasValue
            ? rows
                .GroupBy(row => new StatusPeriodKey(
                        Date(row, config.StatusBeginField),
                        Date(row, config.StatusEndField),
                        Text(row, config.StatusField)),
                    StatusPeriodKeyComparer.Instance)
                .Sum(group => Average(group, config.ContainerElapsedField) ?? 0m)
            : productionMinutes;

        var containerTargetRaw = FirstDecimal(rows, config.ContainerTargetField);
        var containerTarget = containerTargetRaw.HasValue
            ? Math.Max(containerTargetRaw.Value, config.MinimumContainerTargetMinutes)
            : (decimal?)null;

        var isCurrent = rows.Any(row => Int(row, config.CurrentField) != 0);
        var containerOverdue = isCurrent
                               && containerTarget is > 0m
                               && containerElapsed > containerTarget.Value;

        var activeOperators = rows
            .Where(row => Int(row, config.OperatorActiveField) != 0)
            .Select(row => Text(row, config.OperatorField))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        if (activeOperators.Length == 0)
        {
            activeOperators = rows
                .Select(row => Text(row, config.OperatorField))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        var inspectionState = rows
            .Select(row => Int(row, config.InspectionStateField))
            .DefaultIfEmpty(0)
            .Max();

        var inspection = rows
            .Select(row => Text(row, config.InspectionField))
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";

        var unplanned = (Average(rows, config.UnplannedDowntimeField) ?? 0m) / 60m;

        var warnings = new List<string>();

        if (setupTarget is > 0m && setupActual > setupTarget)
            warnings.Add("Rüstzeit über Soll");

        if (cycleTarget is > 0m && shiftCycleActual > cycleTarget)
            warnings.Add("Schichttakt über Soll");

        if (cycleTarget is > 0m && cycleActual > cycleTarget)
            warnings.Add("Gesamttakt über Soll");

        if (containerOverdue)
            warnings.Add("Behältermeldung überfällig");

        if (inspectionState == 3)
            warnings.Add("Erstmusterprüfung negativ");

        var agStatus = Int(latest, config.OperationStatusField);

        return new ProductionTvCard
        {
            Department = Text(latest, config.DepartmentField),
            Machine = Text(latest, config.MachineField),
            Customer = Text(latest, config.CustomerField),
            Article = Text(latest, config.ArticleField),
            ArticleNumber = Text(latest, config.ArticleNumberField),
            Order = Text(latest, config.OrderField),
            OperationNumber = Text(latest, config.OperationNumberField),
            Operation = Text(latest, config.OperationField),
            Status = status,
            StatusColor = statusColor,
            StatusTextColor = ContrastText(statusColor),
            StatusSince = Date(latest, config.StatusBeginField),
            PlannedEnd = Date(latest, config.PlannedEndField),
            OperationStatus = agStatus,
            OperationStatusIcon = agStatus switch
            {
                3 => "▶",
                5 => "⏸",
                7 => "✓",
                _ => "●"
            },
            Operators = string.Join(", ", activeOperators),
            Inspection = inspection,
            InspectionState = inspectionState,
            InspectionClass = inspectionState switch
            {
                1 => "good",
                2 => "warn",
                3 => "bad",
                _ => "neutral"
            },
            Setup = Metric(setupActual, setupTarget, "h", decimals: 2),
            ShiftCycle = Metric(shiftCycleActual, cycleTarget, "min", decimals: 2),
            ShiftQuantity = Metric(releasedQuantity, shiftTargetQuantity, "Stk", decimals: 0, lowerIsBetter: false),
            TotalCycle = Metric(cycleActual, cycleTarget, "min", decimals: 2),
            ContainerCycle = Metric(containerElapsed, containerTarget, "min", decimals: 0),
            LastContainerAt = lastContainerDate,
            LastContainerQuantity = lastContainerRow is null
                ? null
                : Decimal(lastContainerRow, config.ReleasedField),
            LastContainerReporter = lastContainerRow is null
                ? ""
                : Text(lastContainerRow, config.ContainerReporterField),
            UnplannedDowntimeHours = Math.Round(unplanned, 2),
            ContainerOverdue = containerOverdue,
            Warnings = warnings
        };
    }

    private static ProductionTvMetric Metric(
        decimal? actual,
        decimal? target,
        string unit,
        int decimals,
        bool lowerIsBetter = true)
    {
        var health = "neutral";

        if (actual.HasValue && target is > 0m)
        {
            var ratio = actual.Value / target.Value;

            if (!lowerIsBetter)
            {
                health = ratio >= 1m ? "good" : ratio >= 0.9m ? "warn" : "bad";
            }
            else
            {
                health = ratio <= 1m ? "good" : ratio <= 1.1m ? "warn" : "bad";
            }
        }

        return new ProductionTvMetric
        {
            Actual = actual,
            Target = target,
            Unit = unit,
            Health = health,
            ActualText = NumberText(actual, decimals),
            TargetText = NumberText(target, decimals)
        };
    }

    private static string NumberText(decimal? value, int decimals) =>
        value.HasValue
            ? value.Value.ToString($"N{decimals}", DeAt)
            : "–";

    private static string ContrastText(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#')
            return "#263d45";

        try
        {
            var r = Convert.ToInt32(hex.Substring(1, 2), 16);
            var g = Convert.ToInt32(hex.Substring(3, 2), 16);
            var b = Convert.ToInt32(hex.Substring(5, 2), 16);
            var luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255d;
            return luminance < 0.55 ? "#ffffff" : "#263d45";
        }
        catch
        {
            return "#263d45";
        }
    }

    private static decimal? FirstDecimal(
        IEnumerable<Dictionary<string, object?>> rows,
        string field) =>
        rows.Select(row => Decimal(row, field)).FirstOrDefault(x => x.HasValue);

    private static decimal? Average(
        IEnumerable<Dictionary<string, object?>> rows,
        string field)
    {
        var values = rows
            .Select(row => Decimal(row, field))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToArray();

        return values.Length == 0 ? null : values.Average();
    }

    private static string Text(
        IReadOnlyDictionary<string, object?> row,
        string field) =>
        Convert.ToString(QueryResult.Get(row, field), DeAt)?.Trim() ?? "";

    private static decimal? Decimal(
        IReadOnlyDictionary<string, object?> row,
        string field)
    {
        var value = QueryResult.Get(row, field);
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

    private static int Int(
        IReadOnlyDictionary<string, object?> row,
        string field) =>
        Decimal(row, field) is decimal value
            ? (int)Math.Round(value)
            : 0;

    private static DateTime? Date(
        IReadOnlyDictionary<string, object?> row,
        string field)
    {
        var value = QueryResult.Get(row, field);
        if (value is null)
            return null;

        if (value is DateTime date)
            return date;

        if (value is DateTimeOffset dto)
            return dto.DateTime;

        return DateTime.TryParse(
            Convert.ToString(value, DeAt),
            DeAt,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed
            : null;
    }

    private sealed record ProductionGroupKey(
        string Department,
        string Machine,
        string Order,
        string Article,
        string OperationNumber);

    private sealed class ProductionGroupKeyComparer : IEqualityComparer<ProductionGroupKey>
    {
        public static ProductionGroupKeyComparer Instance { get; } = new();

        public bool Equals(ProductionGroupKey? x, ProductionGroupKey? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;

            return string.Equals(x.Department, y.Department, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Machine, y.Machine, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Order, y.Order, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Article, y.Article, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.OperationNumber, y.OperationNumber, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(ProductionGroupKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Department ?? ""),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Machine ?? ""),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Order ?? ""),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Article ?? ""),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.OperationNumber ?? ""));
    }

    private sealed record StatusPeriodKey(
        DateTime? Begin,
        DateTime? End,
        string Status);

    private sealed class StatusPeriodKeyComparer : IEqualityComparer<StatusPeriodKey>
    {
        public static StatusPeriodKeyComparer Instance { get; } = new();

        public bool Equals(StatusPeriodKey? x, StatusPeriodKey? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;

            return x.Begin == y.Begin
                && x.End == y.End
                && string.Equals(x.Status, y.Status, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(StatusPeriodKey obj) =>
            HashCode.Combine(
                obj.Begin,
                obj.End,
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Status ?? ""));
    }
}
