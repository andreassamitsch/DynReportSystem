using System.Text;

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

            if (normalizedSet.Equals("Customer", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(normalizedKey))
                return StableCustomerColor(normalizedKey);
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

    private static string StableCustomerColor(string key)
    {
        var normalized = string.Join(
            " ",
            key.Normalize(NormalizationForm.FormKC)
                .Trim()
                .ToUpperInvariant()
                .Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in normalized)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            var hue = hash % 360;
            var saturation = 0.58 + ((hash >> 9) % 10) / 100d;
            var lightness = 0.45 + ((hash >> 17) % 8) / 100d;

            // Yellow/lime and cyan need slightly darker / calmer values on white
            // dashboard backgrounds. This keeps customer colors readable instead
            // of drifting into neon or near-white tones.
            if (hue is >= 42 and <= 82)
            {
                saturation = Math.Min(saturation, 0.62);
                lightness = Math.Max(0.40, lightness - 0.05);
            }
            else if (hue is >= 83 and <= 155)
            {
                lightness = Math.Max(0.42, lightness - 0.03);
            }
            else if (hue is >= 170 and <= 205)
            {
                saturation = Math.Min(saturation, 0.60);
                lightness = Math.Max(0.43, lightness - 0.02);
            }

            return HslToHex(hue, saturation, lightness);
        }
    }

    private static string HslToHex(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var h = hue / 60d;
        var x = chroma * (1 - Math.Abs(h % 2 - 1));

        var (r1, g1, b1) = h switch
        {
            < 1 => (chroma, x, 0d),
            < 2 => (x, chroma, 0d),
            < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma),
            < 5 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };

        var m = lightness - chroma / 2;
        var r = (int)Math.Round((r1 + m) * 255);
        var g = (int)Math.Round((g1 + m) * 255);
        var b = (int)Math.Round((b1 + m) * 255);

        return $"#{Math.Clamp(r, 0, 255):X2}{Math.Clamp(g, 0, 255):X2}{Math.Clamp(b, 0, 255):X2}";
    }

    private static bool IsHex(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length == 7
        && value[0] == '#'
        && value.Skip(1).All(Uri.IsHexDigit);
}
