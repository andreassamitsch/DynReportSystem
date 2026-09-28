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
                if (ShouldDeferDataSet(package, dataSet.Id))
                    continue;

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


    public async Task<DynTableQueryResult> QueryTableAsync(
        LoadedDynReportPackage package,
        string visualId,
        DynTableQueryRequest request,
        string? userName = null,
        CancellationToken cancellationToken = default)
    {
        var visual = package.Document.Pages
            .SelectMany(page => page.Components)
            .FirstOrDefault(component =>
                component.Id.Equals(visualId, StringComparison.OrdinalIgnoreCase)
                && component.Type.Equals("table", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Tabelle '{visualId}' wurde im Bericht nicht gefunden.");

        var table = visual.Table ?? new DynTableDefinition();
        var dataSet = package.Document.Datasets.FirstOrDefault(x =>
            x.Id.Equals(visual.Dataset, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Dataset '{visual.Dataset}' der Tabelle '{visualId}' wurde nicht gefunden.");

        if (!package.TextFiles.TryGetValue(dataSet.QueryFile, out var sql)
            || string.IsNullOrWhiteSpace(sql))
        {
            throw new InvalidDataException(
                $"SQL-Datei '{dataSet.QueryFile}' für Dataset '{dataSet.Id}' fehlt oder ist leer.");
        }

        queryPolicy.Validate(dataSet, sql);
        sql = NormalizeComposableSelect(dataSet, sql);

        var source = package.Document.DataSources.FirstOrDefault(x =>
            x.Id.Equals(dataSet.DataSourceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Datenquelle '{dataSet.DataSourceId}' wurde nicht gefunden.");

        if (!source.Provider.Equals("SQL", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"Datenquellen-Provider '{source.Provider}' wird für serverseitige Tabellenabfragen nicht unterstützt.");

        var parameters = CreateInitialValues(package);
        foreach (var parameter in package.Document.Parameters)
        {
            if (!request.Parameters.TryGetValue(parameter.Name, out var supplied))
                continue;

            var state = parameters[parameter.Name];
            state.Values.Clear();
            state.Values.AddRange(supplied.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        var sqlParameters = new List<SqlParameter>();
        foreach (var binding in dataSet.Parameters)
        {
            if (!parameters.TryGetValue(binding.ParameterName, out var value))
            {
                sqlParameters.Add(new SqlParameter(binding.SqlName, DBNull.Value));
                continue;
            }

            if ((binding.MultiValue || value.MultiValue)
                && value.Values.Count > 0)
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

        var configuredFields = dataSet.Fields
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (configuredFields.Count == 0)
        {
            foreach (var column in table.Columns)
            {
                if (!string.IsNullOrWhiteSpace(column.Field))
                    configuredFields.Add(column.Field);
            }
        }

        if (configuredFields.Count == 0)
            throw new InvalidDataException(
                $"Dataset '{dataSet.Id}' benötigt Felddefinitionen für serverseitige Tabellenabfragen.");

        var allowedColumns = table.Columns
            .Where(x => !x.Hidden && !string.IsNullOrWhiteSpace(x.Field))
            .Select(x => x.Field)
            .Where(configuredFields.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (allowedColumns.Count == 0)
            allowedColumns = configuredFields;

        var where = new List<string>();
        var filterIndex = 0;

        if (table.Searchable && !string.IsNullOrWhiteSpace(request.Search))
        {
            var searchColumns = allowedColumns.Take(32).ToArray();
            if (searchColumns.Length > 0)
            {
                var searchName = "@__dyn_search";
                var pieces = searchColumns
                    .Select(field =>
                        $"TRY_CONVERT(nvarchar(4000), src.{QuoteIdentifier(field)}) LIKE {searchName} ESCAPE '\\'")
                    .ToArray();

                where.Add("(" + string.Join(" OR ", pieces) + ")");
                sqlParameters.Add(new SqlParameter(searchName, SqlDbType.NVarChar, 4000)
                {
                    Value = $"%{EscapeLike(request.Search.Trim())}%"
                });
            }
        }

        if (table.Filterable)
        {
            foreach (var filter in request.Filters.Take(32))
            {
                if (string.IsNullOrWhiteSpace(filter.Field)
                    || !allowedColumns.Contains(filter.Field))
                    continue;

                var column = table.Columns.FirstOrDefault(x =>
                    x.Field.Equals(filter.Field, StringComparison.OrdinalIgnoreCase));

                if (column?.Filterable == false)
                    continue;

                var value = filter.Value?.Trim() ?? "";
                if (value.Length == 0)
                    continue;

                var parameterName = $"@__dyn_f{filterIndex++}";
                var fieldSql = $"src.{QuoteIdentifier(filter.Field)}";
                var op = (filter.Operator ?? "contains").Trim().ToLowerInvariant();

                switch (op)
                {
                    case "eq":
                        where.Add($"{fieldSql} = {parameterName}");
                        sqlParameters.Add(CreateFilterParameter(parameterName, column?.DataType, value));
                        break;
                    case "ne":
                        where.Add($"{fieldSql} <> {parameterName}");
                        sqlParameters.Add(CreateFilterParameter(parameterName, column?.DataType, value));
                        break;
                    case "gt":
                    case "gte":
                    case "lt":
                    case "lte":
                        var sqlOperator = op switch
                        {
                            "gt" => ">",
                            "gte" => ">=",
                            "lt" => "<",
                            _ => "<="
                        };
                        where.Add($"{fieldSql} {sqlOperator} {parameterName}");
                        sqlParameters.Add(CreateFilterParameter(parameterName, column?.DataType, value));
                        break;
                    case "startswith":
                    case "endswith":
                    case "contains":
                    default:
                        var pattern = op switch
                        {
                            "startswith" => $"{EscapeLike(value)}%",
                            "endswith" => $"%{EscapeLike(value)}",
                            _ => $"%{EscapeLike(value)}%"
                        };
                        where.Add(
                            $"TRY_CONVERT(nvarchar(4000), {fieldSql}) LIKE {parameterName} ESCAPE '\\'");
                        sqlParameters.Add(new SqlParameter(parameterName, SqlDbType.NVarChar, 4000)
                        {
                            Value = pattern
                        });
                        break;
                }
            }
        }

        var requestedSort = table.Sortable
            ? request.Sort
                .Where(x => allowedColumns.Contains(x.Field))
                .Where(x => table.Columns.FirstOrDefault(c =>
                    c.Field.Equals(x.Field, StringComparison.OrdinalIgnoreCase))?.Sortable != false)
                .Take(8)
                .ToArray()
            : [];

        var chosenSort = requestedSort.Length > 0
            ? requestedSort
            : table.DefaultSort
                .Where(x => allowedColumns.Contains(x.Field))
                .Take(8)
                .ToArray();

        var effectiveSort = table.GroupBy
            .Where(allowedColumns.Contains)
            .Select(groupField => new DynSortDefinition { Field = groupField, Direction = "Asc" })
            .Concat(chosenSort)
            .GroupBy(x => x.Field, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(8)
            .ToArray();

        var orderBy = effectiveSort.Length > 0
            ? string.Join(", ", effectiveSort.Select(x =>
                $"src.{QuoteIdentifier(x.Field)} {(x.Direction.Equals("Desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC")}"))
            : $"src.{QuoteIdentifier(allowedColumns.First())} ASC";

        var maxPageSize = Math.Clamp(
            config.GetValue("Runtime:MaxInteractivePageSize", 1000),
            50,
            5000);
        var limit = Math.Clamp(
            request.Limit > 0 ? request.Limit : table.PageSize,
            1,
            maxPageSize);
        var offset = Math.Max(0, request.Offset);

        var finalSql = $"""
            SELECT src.*, COUNT_BIG(1) OVER() AS [__dyn_total]
            FROM (
            {sql}
            ) AS src
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY {orderBy}
            OFFSET @__dyn_offset ROWS FETCH NEXT @__dyn_limit ROWS ONLY
            """;

        sqlParameters.Add(new SqlParameter("@__dyn_offset", SqlDbType.Int) { Value = offset });
        sqlParameters.Add(new SqlParameter("@__dyn_limit", SqlDbType.Int) { Value = limit });

        var timeout = Math.Clamp(
            package.Document.Settings.CommandTimeoutSeconds > 0
                ? package.Document.Settings.CommandTimeoutSeconds
                : config.GetValue("Portal:CommandTimeoutSeconds", 120),
            5,
            Math.Clamp(config.GetValue("Runtime:AbsoluteMaxQuerySeconds", 300), 30, 900));

        var stopwatch = Stopwatch.StartNew();
        using var gateLease = await executionGate.EnterQueryAsync(cancellationToken);

        await using var connection = new SqlConnection(
            dataSources.ResolveConnectionString(source));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(finalSql, connection)
        {
            CommandTimeout = timeout,
            CommandType = CommandType.Text
        };

        foreach (var parameter in sqlParameters)
            command.Parameters.Add(parameter);

        var rows = new List<Dictionary<string, object?>>();
        var columns = new List<string>();
        long totalRows = 0;

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess,
            cancellationToken);

        if (reader.FieldCount == 0)
        {
            return new DynTableQueryResult
            {
                Dataset = dataSet.Id,
                Columns = [],
                Rows = rows,
                TotalRows = 0,
                Offset = offset,
                Limit = limit
            };
        }

        var totalOrdinal = -1;
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (name.Equals("__dyn_total", StringComparison.OrdinalIgnoreCase))
                totalOrdinal = i;
            else
                columns.Add(name);
        }

        while (await reader.ReadAsync(cancellationToken))
        {
            if (totalOrdinal >= 0 && totalRows == 0 && !await reader.IsDBNullAsync(totalOrdinal, cancellationToken))
                totalRows = Convert.ToInt64(reader.GetValue(totalOrdinal), CultureInfo.InvariantCulture);

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i == totalOrdinal)
                    continue;

                var name = reader.GetName(i);
                row[name] = await reader.IsDBNullAsync(i, cancellationToken)
                    ? null
                    : reader.GetValue(i);
            }

            rows.Add(row);
        }

        stopwatch.Stop();

        metrics.InteractiveTableQueries.Add(
            1,
            new KeyValuePair<string, object?>("report.id", package.Manifest.ReportId),
            new KeyValuePair<string, object?>("dataset.id", dataSet.Id));
        metrics.InteractiveTableDurationMs.Record(
            stopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("report.id", package.Manifest.ReportId),
            new KeyValuePair<string, object?>("dataset.id", dataSet.Id));
        metrics.InteractiveTableRows.Record(
            rows.Count,
            new KeyValuePair<string, object?>("report.id", package.Manifest.ReportId),
            new KeyValuePair<string, object?>("dataset.id", dataSet.Id));

        var slowThreshold = Math.Max(
            250,
            config.GetValue("Runtime:SlowQueryThresholdMs", 2000));
        if (stopwatch.ElapsedMilliseconds >= slowThreshold)
        {
            logger.LogWarning(
                "Slow interactive DynReport table query {ReportId}/{DatasetId}/{VisualId}: {DurationMs} ms, {RowCount}/{TotalRows} rows",
                package.Manifest.ReportId,
                dataSet.Id,
                visual.Id,
                stopwatch.ElapsedMilliseconds,
                rows.Count,
                totalRows);
        }

        await metadata.WriteAuditAsync(
            new DynAuditEvent(
                Guid.NewGuid(),
                "table.query",
                "success",
                userName,
                package.Manifest.ReportId,
                DataSourceId: dataSet.DataSourceId,
                DatasetId: dataSet.Id,
                DurationMs: stopwatch.ElapsedMilliseconds,
                RowCount: rows.Count,
                DetailsJson: DynAuditEvent.Details(new
                {
                    VisualId = visual.Id,
                    Offset = offset,
                    Limit = limit,
                    TotalRows = totalRows,
                    FilterCount = request.Filters.Count,
                    SortCount = effectiveSort.Length,
                    Search = !string.IsNullOrWhiteSpace(request.Search)
                })),
            cancellationToken);

        return new DynTableQueryResult
        {
            Dataset = dataSet.Id,
            Columns = columns,
            Rows = rows,
            TotalRows = totalRows,
            Offset = offset,
            Limit = limit
        };
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


    private static bool ShouldDeferDataSet(
        LoadedDynReportPackage package,
        string dataSetId)
    {
        var consumers = package.Document.Pages
            .SelectMany(page => page.Components)
            .Where(component =>
                component.Dataset.Equals(dataSetId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (consumers.Length == 0)
            return false;

        return consumers.All(component =>
            component.Type.Equals("table", StringComparison.OrdinalIgnoreCase)
            && string.Equals(component.Table?.DataMode, "Server", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeComposableSelect(
        DynReportDataset dataSet,
        string sql)
    {
        if (!dataSet.CommandType.Equals("Text", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Dataset '{dataSet.Id}' muss für serverseitiges Paging SQL-Text verwenden.");
        }

        var normalized = sql.Trim();
        if (normalized.EndsWith(';'))
            normalized = normalized[..^1].TrimEnd();

        if (!Regex.IsMatch(
                normalized,
                @"^SELECT\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            throw new NotSupportedException(
                $"Dataset '{dataSet.Id}' ist nicht als komponierbare SELECT-Abfrage aufgebaut. " +
                "Für DataMode=Server muss die SQL-Datei direkt mit SELECT beginnen.");
        }

        if (normalized.Contains(';'))
        {
            throw new NotSupportedException(
                $"Dataset '{dataSet.Id}' enthält mehrere SQL-Anweisungen und kann nicht serverseitig komponiert werden.");
        }

        if (Regex.IsMatch(
                normalized,
                @"\bORDER\s+BY\b|\bFOR\s+(XML|JSON)\b|\bOPTION\s*\(",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            throw new NotSupportedException(
                $"Dataset '{dataSet.Id}' enthält ORDER BY/FOR/OPTION auf oberster Abfrageebene. " +
                "Sortierung muss bei DataMode=Server über die semantische Tabellendefinition erfolgen.");
        }

        return normalized;
    }

    private static string QuoteIdentifier(string field) =>
        "[" + field.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string EscapeLike(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);

    private static SqlParameter CreateFilterParameter(
        string name,
        string? dataType,
        string value)
    {
        return (dataType ?? "Auto").ToUpperInvariant() switch
        {
            "INTEGER" or "INT" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                => new SqlParameter(name, SqlDbType.Int) { Value = integer },
            "DECIMAL" or "FLOAT" or "NUMBER"
                when decimal.TryParse(value, NumberStyles.Any, CultureInfo.GetCultureInfo("de-AT"), out var number)
                => new SqlParameter(name, SqlDbType.Decimal) { Value = number },
            "DATE" or "DATETIME"
                when DateTime.TryParse(value, CultureInfo.GetCultureInfo("de-AT"), DateTimeStyles.AllowWhiteSpaces, out var date)
                => new SqlParameter(name, SqlDbType.DateTime2) { Value = date },
            "BOOLEAN" or "BOOL"
                when bool.TryParse(value, out var boolean)
                => new SqlParameter(name, SqlDbType.Bit) { Value = boolean },
            _ => new SqlParameter(name, SqlDbType.NVarChar, Math.Max(50, Math.Min(4000, value.Length + 20)))
            {
                Value = value
            }
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
