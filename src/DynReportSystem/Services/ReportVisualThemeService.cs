namespace DynReportSystem.Services;

public sealed class ReportVisualThemeService(IConfiguration config)
{
    private static readonly IReadOnlyDictionary<string, string> BuiltInBusinessArea =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["999"] = "#222222",
            ["00"] = "#b5b5b5",
            ["03"] = "#b5b5b5",
            [""] = "#b5b5b5",
            ["Allgemein"] = "#b5b5b5",
            ["Allgm."] = "#b5b5b5",
            ["10"] = "#90ceff",
            ["CNC"] = "#90ceff",
            ["11"] = "#cebbe9",
            ["US"] = "#cebbe9",
            ["12"] = "#efe68c",
            ["Zerodur"] = "#efe68c",
            ["13"] = "#6aaa60",
            ["Keramik"] = "#6aaa60",
            ["21"] = "#7ae5da",
            ["FAM"] = "#7ae5da",
            ["22"] = "#83fcb9",
            ["FAM+"] = "#83fcb9",
            ["31"] = "#ffc65c",
            ["HEPHAX"] = "#ffc65c",
            ["102"] = "#3398FF",
            ["103"] = "#00FFFF",
            ["104"] = "#FF9833"
        };

    public string Resolve(
        string? colorSet,
        string? key,
        IReadOnlyDictionary<string, string>? overrides = null,
        string fallback = "#b5b5b5")
    {
        var normalizedSet = string.IsNullOrWhiteSpace(colorSet)
            ? ""
            : colorSet.Trim();
        var normalizedKey = (key ?? "").Trim();

        if (overrides is not null
            && overrides.TryGetValue(normalizedKey, out var overrideColor)
            && IsHex(overrideColor))
            return overrideColor;

        if (!string.IsNullOrWhiteSpace(normalizedSet))
        {
            var configured = config[$"VisualTheme:ColorSets:{normalizedSet}:{normalizedKey}"];
            if (IsHex(configured))
                return configured!;

            var configuredDefault = config[$"VisualTheme:ColorSets:{normalizedSet}:_default"];
            if (IsHex(configuredDefault))
                fallback = configuredDefault!;

            if (normalizedSet.Equals("BusinessArea", StringComparison.OrdinalIgnoreCase)
                && BuiltInBusinessArea.TryGetValue(normalizedKey, out var builtIn))
                return builtIn;
        }

        return fallback;
    }

    public bool TryResolve(
        string? colorSet,
        string? key,
        out string color,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        color = Resolve(colorSet, key, overrides, "");
        return IsHex(color);
    }

    public IReadOnlyDictionary<string, string> BusinessAreaColors()
    {
        var result = new Dictionary<string, string>(
            BuiltInBusinessArea,
            StringComparer.OrdinalIgnoreCase);

        foreach (var child in config
            .GetSection("VisualTheme:ColorSets:BusinessArea")
            .GetChildren())
        {
            if (child.Key.Equals("_default", StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsHex(child.Value))
                result[child.Key] = child.Value!;
        }

        return result;
    }

    private static bool IsHex(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length == 7
        && value[0] == '#'
        && value.Skip(1).All(Uri.IsHexDigit);
}
