using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DynReportSystem.Models;
using Microsoft.Data.SqlClient;

namespace DynReportSystem.Services;

public sealed class DynReportPackageExecutionService(
    IConfiguration config,
    DynReportDataSourceRegistry dataSources,
    DynReportSqlPolicyValidator queryPolicy,
    DynReportMetadataStore metadata,
    DynReportExecutionGate executionGate,
    DynReportMetrics metrics,
    ILogger<DynReportPackageExecutionService> logger)
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
        string? userName = null,
        CancellationToken cancellationToken = default)
    {
        var run = new DynReportRun
        {
            ExecutionId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            StartedUtc = DateTime.UtcNow
        };

        var total = Stopwatch.StartNew();
        metrics.ReportExecutions.Add(
            1,
            new KeyValuePair<string, object?>("report.id", package.Manifest.ReportId));

        try
        {
            try
            {
                await metadata.RegisterExecutionStartAsync(
                    run.ExecutionId,
                    run.CorrelationId,
                    package.Manifest.ReportId,
                    userName,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not register DynReport execution start for {ReportId}",
                    package.Manifest.ReportId);
            }

            foreach (var dataSet in package.Document.Datasets)
            {
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    var result = await ExecuteDataSetAsync(
                        package,
                        dataSet,
                        parameters,
                        cancellationToken);

                    run.Results[dataSet.Id] = result;

                    metrics.DatasetExecutions.Add(
                        1,
                        new KeyValuePair<string, object?>("report.id", package.Manifest.ReportId),
                        new KeyValuePair<string, object?>("dataset.id", dataSet.Id));
                    metrics.DatasetDurationMs.Record(
                        stopwatch.Elapsed.TotalMilliseconds,
                        new KeyValuePair<string, object?>("report.id", package.Manifest.ReportId),
                        new KeyValuePair<string, object?>("dataset.id", dataSet.Id));
                    metrics.DatasetRows.Record(
                        result.Rows.Count,
                        new KeyValuePair<string, object?>("report.id", package.Manifest.ReportId),
                        new KeyValuePair<string, object?>("dataset.id", dataSet.Id));

                    await metadata.WriteAuditAsync(
                        new DynAuditEvent(
                            run.CorrelationId,
                            "dataset.execute",
                            "success",
                            userName,
                            package.Manifest.ReportId,
                            DataSourceId: dataSet.DataSourceId,
                            DatasetId: dataSet.Id,
                            DurationMs: stopwatch.ElapsedMilliseconds,
                            RowCount: result.Rows.Count,
                            DetailsJson: DynAuditEvent.Details(new
                            {
                                result.Truncated,
                                MaxRows = package.Document.Settings.MaxRowsPerDataset
                            })),
                        cancellationToken);

                    var slowThreshold = Math.Max(
                        250,
                        config.GetValue("Runtime:SlowQueryThresholdMs", 2000));

                    if (stopwatch.ElapsedMilliseconds >= slowThreshold)
                    {
                        logger.LogWarning(
                            "Slow DynReport dataset {ReportId}/{DatasetId}: {DurationMs} ms, {RowCount} rows",
                            package.Manifest.ReportId,
                            dataSet.Id,
                            stopwatch.ElapsedMilliseconds,
                            result.Rows.Count);
                    }
                }
                catch (OperationCanceledException)
                {
                    await metadata.WriteAuditAsync(
                        new DynAuditEvent(
                            run.CorrelationId,
                            "dataset.execute",
                            "cancelled",
                            userName,
                            package.Manifest.ReportId,
                            DataSourceId: dataSet.DataSourceId,
                            DatasetId: dataSet.Id,
                            DurationMs: stopwatch.ElapsedMilliseconds),
                        CancellationToken.None);

                    throw;
                }
                catch (Exception ex)
                {
                    run.Errors.Add($"{dataSet.Id}: {ex.Message}");
                    metrics.DatasetErrors.Add(
                        1,
                        new KeyValuePair<string, object?>("report.id", package.Manifest.ReportId),
                        new KeyValuePair<string, object?>("dataset.id", dataSet.Id));

                    await metadata.WriteAuditAsync(
                        new DynAuditEvent(
                            run.CorrelationId,
                            "dataset.execute",
                            "error",
                            userName,
                            package.Manifest.ReportId,
                            DataSourceId: dataSet.DataSourceId,
                            DatasetId: dataSet.Id,
                            DurationMs: stopwatch.ElapsedMilliseconds,
                            DetailsJson: DynAuditEvent.Details(new
                            {
                                ErrorType = ex.GetType().Name
                            })),
                        cancellationToken);

                    logger.LogError(
                        ex,
                        "DynReport dataset failed {ReportId}/{DatasetId}",
                        package.Manifest.ReportId,
                        dataSet.Id);
                }
            }

            return run;
        }
        finally
        {
            total.Stop();
            run.DurationMs = total.ElapsedMilliseconds;
            metrics.ReportDurationMs.Record(
                total.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("report.id", package.Manifest.ReportId));

            await metadata.RegisterExecutionEndAsync(
                run.ExecutionId,
                run.DurationMs,
                run.Errors.Count,
                CancellationToken.None);

            logger.LogInformation(
                "DynReport execution {ExecutionId} for {ReportId} finished in {DurationMs} ms with {ErrorCount} errors",
                run.ExecutionId,
                package.Manifest.ReportId,
                run.DurationMs,
                run.Errors.Count);
        }
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

        var result = await ExecuteDataSetAsync(
            package,
            dataSet,
            parameters,
            cancellationToken);

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

        queryPolicy.Validate(dataSet, sql);

        var source = package.Document.DataSources.FirstOrDefault(x =>
            x.Id.Equals(dataSet.DataSourceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Datenquelle '{dataSet.DataSourceId}' wurde nicht gefunden.");

        if (!source.Provider.Equals("SQL", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"Datenquellen-Provider '{source.Provider}' wird noch nicht unterstützt.");

        var connectionString = dataSources.ResolveConnectionString(source);
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
            Math.Clamp(config.GetValue("Runtime:AbsoluteMaxRows", 100000), 1000, 1000000));

        var timeout = Math.Clamp(
            package.Document.Settings.CommandTimeoutSeconds > 0
                ? package.Document.Settings.CommandTimeoutSeconds
                : config.GetValue("Portal:CommandTimeoutSeconds", 120),
            5,
            Math.Clamp(config.GetValue("Runtime:AbsoluteMaxQuerySeconds", 300), 30, 900));

        using var gateLease = await executionGate.EnterQueryAsync(cancellationToken);

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
        long approximateBytes = 0;
        var maxResultBytes = Math.Clamp(
            config.GetValue<long>("Runtime:AbsoluteMaxResultBytes", 64L * 1024 * 1024),
            4L * 1024 * 1024,
            512L * 1024 * 1024);

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
                var value = await reader.IsDBNullAsync(i, cancellationToken)
                    ? null
                    : reader.GetValue(i);

                row[columns[i]] = value;
                approximateBytes += ApproximateValueBytes(value);
            }

            if (approximateBytes > maxResultBytes)
            {
                truncated = true;
                break;
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

    private static long ApproximateValueBytes(object? value) => value switch
    {
        null => 1,
        string text => 24L + (text.Length * 2L),
        byte[] bytes => 24L + bytes.LongLength,
        char[] chars => 24L + (chars.LongLength * 2L),
        Guid => 16,
        DateTime => 8,
        DateTimeOffset => 16,
        decimal => 16,
        long or ulong or double => 8,
        int or uint or float => 4,
        short or ushort or char => 2,
        byte or sbyte or bool => 1,
        _ => 32
    };

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
