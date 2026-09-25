using System.Text.Json;
using DynReportSystem.Models;

namespace DynReportSystem.Services;

public sealed class DashboardCatalog(IWebHostEnvironment host)
{
    private readonly object _lock = new();
    private DashboardDefinition? _definition;
    private DateTime _lastWriteUtc;

    public DashboardDefinition Get(string id)
    {
        var path = Path.Combine(host.ContentRootPath, "ReportDefinitions", $"{id}.dashboard.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Dashboard-Definition fehlt.", path);

        var stamp = File.GetLastWriteTimeUtc(path);

        lock (_lock)
        {
            if (_definition is not null
                && _definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                && stamp == _lastWriteUtc)
                return _definition;

            var json = File.ReadAllText(path);
            var definition = JsonSerializer.Deserialize<DashboardDefinition>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Dashboard-Definition ist leer.");

            if (!definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Dashboard-ID stimmt nicht mit dem Dateinamen überein.");

            if (definition.Widgets.Any(w => string.IsNullOrWhiteSpace(w.Id)
                                            || string.IsNullOrWhiteSpace(w.Type)
                                            || string.IsNullOrWhiteSpace(w.Dataset)))
                throw new InvalidDataException("Ungültige Widget-Definition.");

            _definition = definition;
            _lastWriteUtc = stamp;
            return definition;
        }
    }
}
