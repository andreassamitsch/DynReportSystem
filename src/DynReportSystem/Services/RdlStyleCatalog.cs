using System.Text.RegularExpressions;

namespace DynReportSystem.Services;

/// <summary>
/// Reads visual conventions from the installed RDL. DynReport doesn't reproduce
/// the SSRS layout, but it deliberately reuses the established business-area
/// color mapping so the same GB has the same color in both systems.
/// </summary>
public sealed partial class RdlStyleCatalog(RdlCatalog rdl)
{
    private readonly object _gate = new();
    private DateTime _loadedUtc;
    private IReadOnlyDictionary<string, string> _businessAreaColors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string BusinessAreaColor(string? key)
    {
        EnsureLoaded();
        var normalized = (key ?? "").Trim();

        return _businessAreaColors.TryGetValue(normalized, out var color)
            ? color
            : "#b5b5b5";
    }

    public string MixWithWhite(string color, double whitePercent)
    {
        if (!TryRgb(color, out var r, out var g, out var b))
            return color;

        var p = Math.Clamp(whitePercent / 100d, 0d, 1d);
        var nr = (int)Math.Round(r * (1 - p) + 255 * p);
        var ng = (int)Math.Round(g * (1 - p) + 255 * p);
        var nb = (int)Math.Round(b * (1 - p) + 255 * p);

        return $"#{nr:X2}{ng:X2}{nb:X2}";
    }

    private void EnsureLoaded()
    {
        var path = rdl.FullPath;
        if (!File.Exists(path))
            return;

        var modified = File.GetLastWriteTimeUtc(path);

        lock (_gate)
        {
            if (modified == _loadedUtc && _businessAreaColors.Count > 0)
                return;

            var text = File.ReadAllText(path);
            var code = CodeBlockRegex().Match(text).Groups["code"].Value;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in BusinessAreaCaseRegex().Matches(code))
            {
                var key = match.Groups["key"].Value.Trim();
                var color = match.Groups["color"].Value.Trim();

                if (HexColorRegex().IsMatch(color))
                    map[key] = color;
            }

            // The RDL defines blank/unknown GB as grey. Keep that behaviour
            // even when a future XML serializer reformats the embedded code.
            map.TryAdd("", "#b5b5b5");

            _businessAreaColors = map;
            _loadedUtc = modified;
        }
    }

    private static bool TryRgb(string color, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (!HexColorRegex().IsMatch(color))
            return false;

        r = Convert.ToInt32(color.Substring(1, 2), 16);
        g = Convert.ToInt32(color.Substring(3, 2), 16);
        b = Convert.ToInt32(color.Substring(5, 2), 16);
        return true;
    }

    [GeneratedRegex(@"<Code>(?<code>.*?)</Code>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"Case\s+""(?<key>.*?)""\s+ColorCode\s*=\s+""(?<color>#[0-9A-Fa-f]{6})""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BusinessAreaCaseRegex();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColorRegex();
}
