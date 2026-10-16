namespace DynReportSystem.Services;

/// <summary>
/// Continuous deviation shading for production metrics. Does not alter the
/// underlying values or their SSRS-compatible report expressions.
/// </summary>
public static class ProductionMetricHeatmap
{
    public static string Style(decimal? actual, decimal? target, string? direction)
    {
        if (!actual.HasValue || target is null or <= 0m)
            return "";

        var higherIsBetter = string.Equals(
            direction, "HigherIsBetter", StringComparison.OrdinalIgnoreCase);
        var deviation = higherIsBetter
            ? (target.Value - actual.Value) / target.Value
            : (actual.Value - target.Value) / target.Value;

        if (deviation <= 0m)
            return "background-color:#c8efcf;color:#183e30";

        var amount = Math.Clamp((double)deviation, 0d, 1d);
        byte red, green, blue;
        if (amount <= 0.10d)
        {
            var t = amount / 0.10d;
            red = Interpolate(255, 255, t);
            green = Interpolate(245, 221, t);
            blue = Interpolate(182, 139, t);
        }
        else
        {
            var t = (amount - 0.10d) / 0.90d;
            red = Interpolate(255, 238, t);
            green = Interpolate(221, 67, t);
            blue = Interpolate(139, 67, t);
        }

        return $"background-color:rgb({red},{green},{blue});color:#2b2323";
    }

    public static decimal? AdverseDeviation(
        decimal? actual, decimal? target, string? direction)
    {
        if (!actual.HasValue || target is null or <= 0m)
            return null;

        var higherIsBetter = string.Equals(
            direction, "HigherIsBetter", StringComparison.OrdinalIgnoreCase);
        return higherIsBetter
            ? (target.Value - actual.Value) / target.Value
            : (actual.Value - target.Value) / target.Value;
    }

    private static byte Interpolate(int from, int to, double fraction) =>
        (byte)Math.Clamp(
            (int)Math.Round(from + (to - from) * fraction),
            0, 255);
}
