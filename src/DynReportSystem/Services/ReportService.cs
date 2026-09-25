using System.Data;
using DynReportSystem.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.SqlClient;

namespace DynReportSystem.Services;

public sealed class ReportService(
    IConfiguration config,
    RdlCatalog catalog,
    ILogger<ReportService> logger,
    FolderAccess access,
    AuthenticationStateProvider auth)
{
    public IReadOnlyDictionary<string, RdlDataset> Datasets => catalog.GetAll();

    public async Task<QueryResult> RunAsync(
        string name,
        ReportFilters filters,
        CancellationToken cancel = default)
    {
        var state = await auth.GetAuthenticationStateAsync();

        if (!access.CanRunDataset(state.User, name))
            throw new UnauthorizedAccessException("Keine Ausführungsberechtigung für diesen Bericht.");

        if (filters.Start.Date > filters.Ende.Date)
            throw new ArgumentException("Startdatum liegt nach dem Enddatum.");

        if (filters.Kunde.Length > 100 || filters.Vertriebsmitarbeiter.Length > 30)
            throw new ArgumentException("Filterwert zu lang.");

        if (!Datasets.TryGetValue(name, out var ds))
            throw new ArgumentException("Dataset nicht vorhanden.");

        var connectionString = config["Cockpit:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "SQL-Verbindungszeichenfolge fehlt in appsettings.Production.json.");

        var maxRows = Math.Clamp(config.GetValue("Cockpit:MaxRowsPerDataset", 20000), 100, 100000);
        var timeout = Math.Clamp(config.GetValue("Cockpit:CommandTimeoutSeconds", 120), 10, 600);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancel);

        await using var command = new SqlCommand(ds.Sql, connection)
        {
            CommandTimeout = timeout,
            CommandType = CommandType.Text
        };

        foreach (var binding in ds.Bindings)
        {
            var parameter = binding.ParameterName.ToUpperInvariant() switch
            {
                "START" => new SqlParameter(binding.SqlName, SqlDbType.DateTime2) { Value = filters.Start.Date },
                "ENDE" => new SqlParameter(binding.SqlName, SqlDbType.DateTime2) { Value = filters.Ende.Date },
                "KUNDE" => new SqlParameter(binding.SqlName, SqlDbType.NVarChar, 100) { Value = filters.Kunde },
                "VERTRIEBSMITARBEITER" => new SqlParameter(binding.SqlName, SqlDbType.NVarChar, 30) { Value = filters.Vertriebsmitarbeiter },
                "EMBED" => new SqlParameter(binding.SqlName, SqlDbType.NVarChar, 5) { Value = "False" },
                "OPANSICHT" or "REKLANSICHT" => new SqlParameter(binding.SqlName, SqlDbType.NVarChar, 30) { Value = "Alle" },
                _ => throw new InvalidDataException("Unerwarteter SQL-Parameter in der RDL.")
            };

            command.Parameters.Add(parameter);
        }

        var rows = new List<Dictionary<string, object?>>();
        var columns = new List<string>();
        var truncated = false;

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancel);

        while (reader.FieldCount == 0 && await reader.NextResultAsync(cancel)) { }

        if (reader.FieldCount == 0)
            return new QueryResult { Dataset = name, Columns = [], Rows = rows, Truncated = false };

        for (var i = 0; i < reader.FieldCount; i++)
            columns.Add(reader.GetName(i));

        while (await reader.ReadAsync(cancel))
        {
            if (rows.Count == maxRows)
            {
                truncated = true;
                break;
            }

            var record = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                record[columns[i]] = await reader.IsDBNullAsync(i, cancel) ? null : reader.GetValue(i);

            rows.Add(record);
        }

        logger.LogInformation(
            "Dataset {Dataset} returned {RowCount} records, truncated={Truncated}",
            name, rows.Count, truncated);

        return new QueryResult
        {
            Dataset = name,
            Columns = columns,
            Rows = rows,
            Truncated = truncated
        };
    }
}
