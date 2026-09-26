using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using DynReportSystem.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.SqlClient;

namespace DynReportSystem.Services;

public sealed class DynamicReportExecutionService(
    IConfiguration config,
    DynamicRdlService rdl,
    ImportedPortalCatalog portal,
    FolderAccess access,
    AuthenticationStateProvider authentication,
    ILogger<DynamicReportExecutionService> logger)
{
    public async Task<DynamicReportRun> RunAsync(
        string reportId,
        IReadOnlyDictionary<string, DynamicParameterValue> parameters,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccessAsync(reportId);

        var definition = rdl.GetDefinition(reportId);
        var selected = definition.BodyDataSets.Count > 0
            ? definition.BodyDataSets
            : definition.DataSets.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var run = new DynamicReportRun();

        foreach (var dataSetName in selected)
        {
            if (!definition.DataSets.TryGetValue(dataSetName, out var dataSet))
                continue;

            try
            {
                run.Results[dataSetName] = await ExecuteAsync(
                    definition,
                    dataSet,
                    parameters,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Imported report {ReportId}, dataset {DataSet} failed.",
                    reportId,
                    dataSetName);

                run.Errors.Add($"{dataSetName}: {Friendly(ex)}");
            }
        }

        return run;
    }

    public async Task<IReadOnlyList<DynamicParameterOption>> GetValidValuesAsync(
        string reportId,
        DynamicReportParameter parameter,
        IReadOnlyDictionary<string, DynamicParameterValue> parameters,
        CancellationToken cancellationToken = default)
    {
        if (parameter.StaticValidValues.Count > 0)
            return parameter.StaticValidValues;

        if (parameter.ValidValuesDataSet is null)
            return [];

        await EnsureAccessAsync(reportId);

        var definition = rdl.GetDefinition(reportId);
        if (!definition.DataSets.TryGetValue(parameter.ValidValuesDataSet.DataSetName, out var dataSet))
            return [];

        var result = await ExecuteAsync(definition, dataSet, parameters, cancellationToken);
        var options = new List<DynamicParameterOption>();

        foreach (var row in result.Rows)
        {
            var rawValue = QueryResult.Get(row, parameter.ValidValuesDataSet.ValueField);
            if (rawValue is null)
                continue;

            var value = Convert.ToString(rawValue, CultureInfo.GetCultureInfo("de-AT")) ?? "";
            var labelField = parameter.ValidValuesDataSet.LabelField;
            var label = string.IsNullOrWhiteSpace(labelField)
                ? value
                : Convert.ToString(QueryResult.Get(row, labelField), CultureInfo.GetCultureInfo("de-AT")) ?? value;

            options.Add(new DynamicParameterOption(value, label));
        }

        return options
            .DistinctBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task ApplyDataSetDefaultsAsync(
        string reportId,
        DynamicReportDefinition definition,
        IReadOnlyDictionary<string, DynamicParameterValue> values,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccessAsync(reportId);

        foreach (var parameter in definition.Parameters.Where(x => x.DefaultDataSet is not null))
        {
            if (!values.TryGetValue(parameter.Name, out var state) || state.Values.Count > 0)
                continue;

            var reference = parameter.DefaultDataSet!;
            if (!definition.DataSets.TryGetValue(reference.DataSetName, out var dataSet))
                continue;

            try
            {
                var result = await ExecuteAsync(definition, dataSet, values, cancellationToken);
                foreach (var row in result.Rows)
                {
                    var value = QueryResult.Get(row, reference.ValueField);
                    if (value is null)
                        continue;

                    state.Values.Add(ToParameterString(value, parameter.DataType));
                    if (!parameter.MultiValue)
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not resolve default dataset {DataSet} for parameter {Parameter}.",
                    reference.DataSetName,
                    parameter.Name);
            }
        }
    }

    public IReadOnlyList<ImportedDataSourceStatus> GetDataSourceStatus()
    {
        return portal.Catalog.DataSources
            .Select(source =>
            {
                var configured = ResolveConfiguredConnectionString(source);
                return new ImportedDataSourceStatus(
                    source.Title,
                    source.Path,
                    source.ConfigKey,
                    source.OriginalConnectString,
                    !string.IsNullOrWhiteSpace(configured));
            })
            .ToArray();
    }

    private async Task<QueryResult> ExecuteAsync(
        DynamicReportDefinition definition,
        DynamicDatasetDefinition dataSet,
        IReadOnlyDictionary<string, DynamicParameterValue> parameters,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dataSet.CommandText))
            return new QueryResult
            {
                Dataset = dataSet.Name,
                Columns = [],
                Rows = [],
                Truncated = false
            };

        var connectionString = ResolveConnectionString(definition, dataSet);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                $"Datenquelle für Dataset '{dataSet.Name}' ist noch nicht konfiguriert.");

        var maxRows = Math.Clamp(
            config.GetValue("Portal:MaxRowsPerDataset",
                config.GetValue("Cockpit:MaxRowsPerDataset", 20000)),
            100,
            100000);

        var timeout = Math.Clamp(
            config.GetValue("Portal:CommandTimeoutSeconds",
                config.GetValue("Cockpit:CommandTimeoutSeconds", 120)),
            10,
            900);

        var sql = dataSet.CommandText;
        var sqlParameters = new List<SqlParameter>();

        foreach (var binding in dataSet.Bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.SqlName))
                continue;

            if (binding.ReportParameter is null
                || !parameters.TryGetValue(binding.ReportParameter, out var value))
            {
                sqlParameters.Add(new SqlParameter(binding.SqlName, DBNull.Value));
                continue;
            }

            var reportParameter = definition.Parameters.FirstOrDefault(x =>
                x.Name.Equals(binding.ReportParameter, StringComparison.OrdinalIgnoreCase));

            if (value.MultiValue && value.Values.Count > 0
                && !dataSet.CommandType.Equals("StoredProcedure", StringComparison.OrdinalIgnoreCase))
            {
                var replacements = new List<string>();

                for (var i = 0; i < value.Values.Count; i++)
                {
                    var parameterName = $"{binding.SqlName}_{i}";
                    replacements.Add(parameterName);
                    sqlParameters.Add(CreateParameter(
                        parameterName,
                        reportParameter?.DataType ?? value.DataType,
                        value.Values[i]));
                }

                sql = ReplaceSqlParameter(sql, binding.SqlName, string.Join(",", replacements));
                continue;
            }

            var scalar = value.Values.FirstOrDefault();
            sqlParameters.Add(CreateParameter(
                binding.SqlName,
                reportParameter?.DataType ?? value.DataType,
                scalar));
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = timeout,
            CommandType = dataSet.CommandType.Equals("StoredProcedure", StringComparison.OrdinalIgnoreCase)
                ? CommandType.StoredProcedure
                : CommandType.Text
        };

        foreach (var parameter in sqlParameters)
            command.Parameters.Add(parameter);

        var rows = new List<Dictionary<string, object?>>();
        var columns = new List<string>();
        var truncated = false;

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess,
            cancellationToken);

        while (reader.FieldCount == 0 && await reader.NextResultAsync(cancellationToken)) { }

        if (reader.FieldCount == 0)
            return new QueryResult
            {
                Dataset = dataSet.Name,
                Columns = [],
                Rows = rows,
                Truncated = false
            };

        for (var i = 0; i < reader.FieldCount; i++)
            columns.Add(reader.GetName(i));

        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= maxRows)
            {
                truncated = true;
                break;
            }

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[columns[i]] = await reader.IsDBNullAsync(i, cancellationToken)
                    ? null
                    : reader.GetValue(i);

            rows.Add(row);
        }

        return new QueryResult
        {
            Dataset = dataSet.Name,
            Columns = columns,
            Rows = rows,
            Truncated = truncated
        };
    }

    private string ResolveConnectionString(
        DynamicReportDefinition definition,
        DynamicDatasetDefinition dataSet)
    {
        if (!string.IsNullOrWhiteSpace(dataSet.DataSourceReference))
            return ResolveReference(dataSet.DataSourceReference);

        if (!string.IsNullOrWhiteSpace(dataSet.DataSourceName)
            && definition.DataSources.TryGetValue(dataSet.DataSourceName, out var local))
        {
            if (!string.IsNullOrWhiteSpace(local.Reference))
                return ResolveReference(local.Reference);

            if (!string.IsNullOrWhiteSpace(local.ConnectString))
                return BuildConnectionFromImported(local.ConnectString, null);
        }

        return config["Cockpit:ConnectionString"] ?? "";
    }

    private string ResolveReference(string reference)
    {
        var source = portal.FindDataSource(reference);
        if (source is null)
            return config["Cockpit:ConnectionString"] ?? "";

        var configured = ResolveConfiguredConnectionString(source);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return BuildConnectionFromImported(source.OriginalConnectString, source);
    }

    private string ResolveConfiguredConnectionString(ImportedDataSource source)
    {
        var direct = config[$"DataSources:{source.ConfigKey}:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        direct = config[$"DataSources:{source.Title}:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        if (source.Title.Contains("Oxaion", StringComparison.OrdinalIgnoreCase))
            return config["Cockpit:ConnectionString"] ?? "";

        return "";
    }

    private string BuildConnectionFromImported(string imported, ImportedDataSource? source)
    {
        if (string.IsNullOrWhiteSpace(imported))
            return "";

        var fallback = config["Cockpit:ConnectionString"];
        if (string.IsNullOrWhiteSpace(fallback))
            return imported;

        try
        {
            var target = new SqlConnectionStringBuilder(imported);
            var credentials = new SqlConnectionStringBuilder(fallback);

            if (credentials.IntegratedSecurity)
            {
                target.IntegratedSecurity = true;
            }
            else
            {
                target.UserID = credentials.UserID;
                target.Password = credentials.Password;
            }

            target.Encrypt = credentials.Encrypt;
            target.TrustServerCertificate = credentials.TrustServerCertificate;
            target.ConnectTimeout = credentials.ConnectTimeout;
            return target.ConnectionString;
        }
        catch
        {
            return imported;
        }
    }

    private static SqlParameter CreateParameter(string name, string type, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new SqlParameter(name, DBNull.Value);

        try
        {
            return type.ToUpperInvariant() switch
            {
                "INTEGER" => new SqlParameter(name, SqlDbType.Int)
                {
                    Value = int.Parse(value, CultureInfo.InvariantCulture)
                },
                "FLOAT" => new SqlParameter(name, SqlDbType.Float)
                {
                    Value = double.Parse(value, NumberStyles.Any, CultureInfo.GetCultureInfo("de-AT"))
                },
                "BOOLEAN" => new SqlParameter(name, SqlDbType.Bit)
                {
                    Value = bool.Parse(value)
                },
                "DATETIME" => new SqlParameter(name, SqlDbType.DateTime2)
                {
                    Value = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces)
                },
                _ => new SqlParameter(name, SqlDbType.NVarChar, Math.Max(50, Math.Min(4000, value.Length + 20)))
                {
                    Value = value
                }
            };
        }
        catch
        {
            return new SqlParameter(name, SqlDbType.NVarChar, Math.Max(50, Math.Min(4000, value.Length + 20)))
            {
                Value = value
            };
        }
    }

    private static string ReplaceSqlParameter(string sql, string parameter, string replacement)
    {
        var pattern = $@"(?<![A-Za-z0-9_]){Regex.Escape(parameter)}(?![A-Za-z0-9_])";
        return Regex.Replace(sql, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ToParameterString(object value, string type)
    {
        if (value is DateTime date)
            return date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        if (value is bool flag)
            return flag ? "true" : "false";

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private async Task EnsureAccessAsync(string reportId)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;

        if (!access.Can(user, reportId, "View") || !access.Can(user, reportId, "Run"))
            throw new UnauthorizedAccessException("Keine Ausführungsberechtigung für diesen Bericht.");
    }

    private static string Friendly(Exception ex) => ex switch
    {
        SqlException sql => sql.Message,
        InvalidOperationException => ex.Message,
        ArgumentException => ex.Message,
        _ => "Dataset konnte nicht ausgeführt werden."
    };
}

public sealed record ImportedDataSourceStatus(
    string Title,
    string Path,
    string ConfigKey,
    string OriginalConnectString,
    bool Configured);
