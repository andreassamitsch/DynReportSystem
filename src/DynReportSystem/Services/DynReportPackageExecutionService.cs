using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using DynReportSystem.Models;
using Microsoft.Data.SqlClient;

namespace DynReportSystem.Services;

public sealed class DynReportPackageExecutionService(IConfiguration config)
{
    public Dictionary<string, DynReportParameterValue> CreateInitialValues(
        LoadedDynReportPackage package)
    {
        var result = new Dictionary<string, DynReportParameterValue>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in package.Document.Parameters)
        {
            var state = new DynReportParameterValue
            {
                Name = parameter.Name,
                DataType = parameter.DataType,
                MultiValue = parameter.MultiValue
            };

            state.Values.AddRange(parameter.DefaultValues);
            result[parameter.Name] = state;
        }

        return result;
    }

    public async Task<DynReportRun> RunAsync(
        LoadedDynReportPackage package,
        IReadOnlyDictionary<string, DynReportParameterValue> parameters,
        CancellationToken cancellationToken = default)
    {
        var run = new DynReportRun();

        foreach (var dataSet in package.Document.Datasets)
        {
            try
            {
                run.Results[dataSet.Id] = await ExecuteDataSetAsync(
                    package,
                    dataSet,
                    parameters,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                run.Errors.Add($"{dataSet.Id}: {ex.Message}");
            }
        }

        return run;
    }

    public async Task<IReadOnlyList<DynReportParameterOption>> GetParameterOptionsAsync(
        LoadedDynReportPackage package,
        DynReportParameter parameter,
        IReadOnlyDictionary<string, DynReportParameterValue> parameters,
        CancellationToken cancellationToken = default)
    {
        if (parameter.Options.Count > 0)
            return parameter.Options;

        if (string.IsNullOrWhiteSpace(parameter.OptionsDataset))
            return [];

        var dataSet = package.Document.Datasets.FirstOrDefault(x =>
            x.Id.Equals(parameter.OptionsDataset, StringComparison.OrdinalIgnoreCase));

        if (dataSet is null)
            return [];

        var result = await ExecuteDataSetAsync(package, dataSet, parameters, cancellationToken);
        var valueField = string.IsNullOrWhiteSpace(parameter.OptionsValueField)
            ? result.Columns.FirstOrDefault() ?? ""
            : parameter.OptionsValueField;
        var labelField = string.IsNullOrWhiteSpace(parameter.OptionsLabelField)
            ? valueField
            : parameter.OptionsLabelField;

        return result.Rows
            .Select(row => new DynReportParameterOption(
                Convert.ToString(QueryResult.Get(row, valueField), CultureInfo.InvariantCulture) ?? "",
                Convert.ToString(QueryResult.Get(row, labelField), CultureInfo.GetCultureInfo("de-AT")) ?? ""))
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .DistinctBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<QueryResult> ExecuteDataSetAsync(
        LoadedDynReportPackage package,
        DynReportDataset dataSet,
        IReadOnlyDictionary<string, DynReportParameterValue> parameters,
        CancellationToken cancellationToken)
    {
        if (!package.TextFiles.TryGetValue(dataSet.QueryFile, out var sql)
            || string.IsNullOrWhiteSpace(sql))
        {
            throw new InvalidDataException(
                $"SQL-Datei '{dataSet.QueryFile}' für Dataset '{dataSet.Id}' fehlt oder ist leer.");
        }

        var source = package.Document.DataSources.FirstOrDefault(x =>
            x.Id.Equals(dataSet.DataSourceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Datenquelle '{dataSet.DataSourceId}' wurde nicht gefunden.");

        if (!source.Provider.Equals("SQL", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"Datenquellen-Provider '{source.Provider}' wird noch nicht unterstützt.");

        var connectionString = ResolveConnectionString(source);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                $"Datenquelle '{source.Id}' ist noch nicht konfiguriert.");

        var sqlParameters = new List<SqlParameter>();

        foreach (var binding in dataSet.Parameters)
        {
            if (!parameters.TryGetValue(binding.ParameterName, out var value))
            {
                sqlParameters.Add(new SqlParameter(binding.SqlName, DBNull.Value));
                continue;
            }

            if ((binding.MultiValue || value.MultiValue)
                && value.Values.Count > 0
                && !dataSet.CommandType.Equals("StoredProcedure", StringComparison.OrdinalIgnoreCase))
            {
                var replacements = new List<string>();

                for (var i = 0; i < value.Values.Count; i++)
                {
                    var parameterName = $"{binding.SqlName}_{i}";
                    replacements.Add(parameterName);
                    sqlParameters.Add(CreateParameter(
                        parameterName,
                        binding.DataType,
                        value.Values[i]));
                }

                sql = ReplaceSqlParameter(sql, binding.SqlName, string.Join(",", replacements));
                continue;
            }

            sqlParameters.Add(CreateParameter(
                binding.SqlName,
                binding.DataType,
                value.Values.FirstOrDefault()));
        }

        var maxRows = Math.Clamp(
            package.Document.Settings.MaxRowsPerDataset > 0
                ? package.Document.Settings.MaxRowsPerDataset
                : config.GetValue("Portal:MaxRowsPerDataset", 20000),
            100,
            100000);

        var timeout = Math.Clamp(
            package.Document.Settings.CommandTimeoutSeconds > 0
                ? package.Document.Settings.CommandTimeoutSeconds
                : config.GetValue("Portal:CommandTimeoutSeconds", 120),
            10,
            900);

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
        {
            return new QueryResult
            {
                Dataset = dataSet.Id,
                Columns = [],
                Rows = rows,
                Truncated = false
            };
        }

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
            {
                row[columns[i]] = await reader.IsDBNullAsync(i, cancellationToken)
                    ? null
                    : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return new QueryResult
        {
            Dataset = dataSet.Id,
            Columns = columns,
            Rows = rows,
            Truncated = truncated
        };
    }

    private string ResolveConnectionString(DynReportDataSource source)
    {
        var direct = config[$"DataSources:{source.ConfigKey}:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        direct = config[$"DataSources:{source.Id}:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        if (string.IsNullOrWhiteSpace(source.DefaultConnectionString))
            return "";

        var fallback = config["Cockpit:ConnectionString"];
        if (string.IsNullOrWhiteSpace(fallback))
            return source.DefaultConnectionString;

        try
        {
            var target = new SqlConnectionStringBuilder(source.DefaultConnectionString);
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
            return source.DefaultConnectionString;
        }
    }

    private static SqlParameter CreateParameter(
        string name,
        string type,
        string? value)
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
                "FLOAT" or "DECIMAL" => new SqlParameter(name, SqlDbType.Decimal)
                {
                    Value = decimal.Parse(value, NumberStyles.Any, CultureInfo.GetCultureInfo("de-AT"))
                },
                "BOOLEAN" => new SqlParameter(name, SqlDbType.Bit)
                {
                    Value = value is "-1" or "1"
                        || bool.TryParse(value, out var boolean) && boolean
                },
                "DATETIME" => new SqlParameter(name, SqlDbType.DateTime2)
                {
                    Value = DateTime.Parse(
                        value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal)
                },
                _ => new SqlParameter(
                    name,
                    SqlDbType.NVarChar,
                    Math.Max(50, Math.Min(4000, value.Length + 20)))
                {
                    Value = value
                }
            };
        }
        catch
        {
            return new SqlParameter(
                name,
                SqlDbType.NVarChar,
                Math.Max(50, Math.Min(4000, value.Length + 20)))
            {
                Value = value
            };
        }
    }

    private static string ReplaceSqlParameter(
        string sql,
        string parameter,
        string replacement)
    {
        var pattern = $@"(?<![A-Za-z0-9_]){Regex.Escape(parameter)}(?![A-Za-z0-9_])";
        return Regex.Replace(
            sql,
            pattern,
            replacement,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
