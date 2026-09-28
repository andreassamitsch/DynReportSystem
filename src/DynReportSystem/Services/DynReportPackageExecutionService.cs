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
        sql = DynReportInteractiveQueryRules.NormalizeComposableSelect(dataSet, sql);

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

        var presentationMode = string.IsNullOrWhiteSpace(table.PresentationMode)
            ? "Rows"
            : table.PresentationMode.Trim();

        var groupedMode = presentationMode.Equals("Grouped", StringComparison.OrdinalIgnoreCase);
        var pivotMode = presentationMode.Equals("Pivot", StringComparison.OrdinalIgnoreCase);

        var maxPageSize = Math.Clamp(
            config.GetValue("Runtime:MaxInteractivePageSize", 1000),
            50,
            5000);
        var limit = Math.Clamp(
            request.Limit > 0 ? request.Limit : table.PageSize,
            1,
            maxPageSize);
        var offset = Math.Max(0, request.Offset);
        var whereClause = where.Count > 0
            ? "WHERE " + string.Join(" AND ", where)
            : "";

        var resolvedAggregates = ResolveAggregates(
            table.Aggregates,
            configuredFields);

        string finalSql;
        IReadOnlyList<string> pivotRowFields = [];
        IReadOnlyList<ResolvedTableAggregate> pivotMeasures = [];
        var pivotMaxColumns = 0;

        if (pivotMode)
        {
            var pivot = table.Pivot
                ?? throw new InvalidDataException(
                    $"Tabelle '{visual.Id}' benötigt für die Pivot-Darstellung eine Pivot-Definition.");

            pivotRowFields = pivot.RowFields
                .Where(configuredFields.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();

            if (pivotRowFields.Count == 0)
            {
                throw new InvalidDataException(
                    $"Tabelle '{visual.Id}' benötigt mindestens ein Pivot-Zeilenfeld.");
            }

            if (string.IsNullOrWhiteSpace(pivot.ColumnField)
                || !configuredFields.Contains(pivot.ColumnField))
            {
                throw new InvalidDataException(
                    $"Pivot-Spaltenfeld '{pivot.ColumnField}' ist im Dataset '{dataSet.Id}' nicht vorhanden.");
            }

            pivotMeasures = ResolveAggregates(
                pivot.Measures.Count > 0 ? pivot.Measures : table.Aggregates,
                configuredFields);

            if (pivotMeasures.Count == 0)
            {
                throw new InvalidDataException(
                    $"Tabelle '{visual.Id}' benötigt mindestens eine Pivot-Kennzahl.");
            }

            pivotMaxColumns = Math.Clamp(pivot.MaxColumns, 1, 60);
            var maxPivotRows = Math.Clamp(
                config.GetValue("Runtime:MaxInteractivePivotRows", 250),
                10,
                2000);
            var pivotRows = Math.Min(limit, maxPivotRows);
            var maxPivotCells = Math.Clamp(
                config.GetValue("Runtime:MaxInteractivePivotCells", 5000),
                100,
                50000);
            var pivotCells = Math.Min(maxPivotCells, pivotRows * pivotMaxColumns);

            sqlParameters.Add(new SqlParameter("@__dyn_pivot_columns", SqlDbType.Int)
            {
                Value = pivotMaxColumns
            });
            sqlParameters.Add(new SqlParameter("@__dyn_pivot_cells", SqlDbType.Int)
            {
                Value = pivotCells
            });

            var pivotColumnSql = $"src.{QuoteIdentifier(pivot.ColumnField)}";
            var rowSelect = string.Join(
                ", ",
                pivotRowFields.Select(field =>
                    $"src.{QuoteIdentifier(field)} AS {QuoteIdentifier(field)}"));
            var groupSql = string.Join(
                ", ",
                pivotRowFields
                    .Select(field => $"src.{QuoteIdentifier(field)}")
                    .Append(pivotColumnSql));
            var orderSql = string.Join(
                ", ",
                pivotRowFields
                    .Select(field => $"src.{QuoteIdentifier(field)} ASC")
                    .Append(
                        $"src.{QuoteIdentifier(pivot.ColumnField)} " +
                        (pivot.ColumnSort.Equals("Desc", StringComparison.OrdinalIgnoreCase)
                            ? "DESC"
                            : "ASC")));
            var measureSql = string.Join(
                ", ",
                pivotMeasures.Select(AggregateSelectSql));

            var pivotWhere = string.IsNullOrWhiteSpace(whereClause)
                ? $"WHERE {pivotColumnSql} IS NOT NULL"
                : $"{whereClause} AND {pivotColumnSql} IS NOT NULL";

            finalSql = $"""
                WITH base AS (
                {sql}
                ),
                pivot_values AS (
                    SELECT TOP (@__dyn_pivot_columns)
                        src.{QuoteIdentifier(pivot.ColumnField)} AS [__dyn_pivot_value]
                    FROM base AS src
                    {pivotWhere}
                    GROUP BY src.{QuoteIdentifier(pivot.ColumnField)}
                    ORDER BY src.{QuoteIdentifier(pivot.ColumnField)}
                        {(pivot.ColumnSort.Equals("Desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC")}
                )
                SELECT TOP (@__dyn_pivot_cells)
                    {rowSelect},
                    TRY_CONVERT(nvarchar(4000), {pivotColumnSql}) AS [__dyn_pivot_column],
                    {measureSql}
                FROM base AS src
                INNER JOIN pivot_values AS pv
                    ON src.{QuoteIdentifier(pivot.ColumnField)} = pv.[__dyn_pivot_value]
                {whereClause}
                GROUP BY {groupSql}
                ORDER BY {orderSql}
                """;

            offset = 0;
            limit = pivotRows;
        }
        else if (groupedMode)
        {
            var groupFields = table.GroupBy
                .Where(configuredFields.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();

            if (groupFields.Length == 0)
            {
                throw new InvalidDataException(
                    $"Tabelle '{visual.Id}' benötigt für die aggregierte Darstellung mindestens ein Gruppierungsfeld.");
            }

            if (resolvedAggregates.Count == 0)
            {
                throw new InvalidDataException(
                    $"Tabelle '{visual.Id}' benötigt für die aggregierte Darstellung mindestens eine Kennzahl.");
            }

            var outputFields = groupFields
                .Concat(resolvedAggregates.Select(x => x.OutputName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var requestedSort = table.Sortable
                ? request.Sort
                    .Where(x => outputFields.Contains(x.Field))
                    .Take(8)
                    .ToArray()
                : [];

            var defaultSort = table.DefaultSort
                .Where(x => outputFields.Contains(x.Field))
                .Take(8)
                .ToArray();

            var chosenSort = requestedSort.Length > 0
                ? requestedSort
                : defaultSort.Length > 0
                    ? defaultSort
                    : groupFields
                        .Select(field => new DynSortDefinition
                        {
                            Field = field,
                            Direction = "Asc"
                        })
                        .ToArray();

            var selectGroups = string.Join(
                ", ",
                groupFields.Select(field =>
                    $"src.{QuoteIdentifier(field)} AS {QuoteIdentifier(field)}"));
            var aggregateSql = string.Join(
                ", ",
                resolvedAggregates.Select(AggregateSelectSql));
            var groupBySql = string.Join(
                ", ",
                groupFields.Select(field => $"src.{QuoteIdentifier(field)}"));
            var orderBySql = string.Join(
                ", ",
                chosenSort.Select(sort =>
                {
                    var expression = groupFields.Contains(
                        sort.Field,
                        StringComparer.OrdinalIgnoreCase)
                            ? $"src.{QuoteIdentifier(sort.Field)}"
                            : QuoteIdentifier(sort.Field);

                    return expression + " " +
                        (sort.Direction.Equals("Desc", StringComparison.OrdinalIgnoreCase)
                            ? "DESC"
                            : "ASC");
                }));

            finalSql = $"""
                SELECT
                    {selectGroups},
                    {aggregateSql},
                    COUNT_BIG(1) OVER() AS [__dyn_total]
                FROM (
                {sql}
                ) AS src
                {whereClause}
                GROUP BY {groupBySql}
                ORDER BY {orderBySql}
                OFFSET @__dyn_offset ROWS FETCH NEXT @__dyn_limit ROWS ONLY
                """;

            sqlParameters.Add(new SqlParameter("@__dyn_offset", SqlDbType.Int)
            {
                Value = offset
            });
            sqlParameters.Add(new SqlParameter("@__dyn_limit", SqlDbType.Int)
            {
                Value = limit
            });
        }
        else
        {
            var requestedSort = table.Sortable
                ? request.Sort
                    .Where(x => allowedColumns.Contains(x.Field))
                    .Where(x => table.Columns.FirstOrDefault(column =>
                        column.Field.Equals(x.Field, StringComparison.OrdinalIgnoreCase))?.Sortable != false)
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
                .Where(configuredFields.Contains)
                .Select(groupField => new DynSortDefinition
                {
                    Field = groupField,
                    Direction = "Asc"
                })
                .Concat(chosenSort)
                .GroupBy(x => x.Field, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(8)
                .ToArray();

            var fallbackSortField = table.Columns
                .Where(x => !x.Hidden && configuredFields.Contains(x.Field))
                .Select(x => x.Field)
                .FirstOrDefault()
                ?? dataSet.Fields.FirstOrDefault(configuredFields.Contains)
                ?? configuredFields
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .First();

            var orderBy = effectiveSort.Length > 0
                ? string.Join(
                    ", ",
                    effectiveSort.Select(x =>
                        $"src.{QuoteIdentifier(x.Field)} " +
                        (x.Direction.Equals("Desc", StringComparison.OrdinalIgnoreCase)
                            ? "DESC"
                            : "ASC")))
                : $"src.{QuoteIdentifier(fallbackSortField)} ASC";

            finalSql = $"""
                SELECT src.*, COUNT_BIG(1) OVER() AS [__dyn_total]
                FROM (
                {sql}
                ) AS src
                {whereClause}
                ORDER BY {orderBy}
                OFFSET @__dyn_offset ROWS FETCH NEXT @__dyn_limit ROWS ONLY
                """;

            sqlParameters.Add(new SqlParameter("@__dyn_offset", SqlDbType.Int)
            {
                Value = offset
            });
            sqlParameters.Add(new SqlParameter("@__dyn_limit", SqlDbType.Int)
            {
                Value = limit
            });
        }

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

        if (pivotMode)
        {
            var shaped = ShapePivotRows(
                rows,
                pivotRowFields,
                pivotMeasures,
                pivotMaxColumns,
                limit);

            rows = shaped.Rows;
            columns = shaped.Columns.ToList();
            totalRows = rows.Count;
            offset = 0;
        }
        else if (totalRows == 0 && rows.Count > 0)
        {
            totalRows = rows.Count;
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
                    SortCount = request.Sort.Count,
                    PresentationMode = presentationMode,
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


    private static IReadOnlyList<ResolvedTableAggregate> ResolveAggregates(
        IEnumerable<DynTableAggregate> definitions,
        IReadOnlySet<string> configuredFields)
    {
        var result = new List<ResolvedTableAggregate>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions.Take(16))
        {
            var function = (definition.Function ?? "Sum").Trim();
            if (function.Length == 0)
                function = "Sum";

            var normalizedFunction = function.ToLowerInvariant() switch
            {
                "sum" => "Sum",
                "avg" or "average" => "Avg",
                "min" => "Min",
                "max" => "Max",
                "count" => "Count",
                "distinctcount" or "countdistinct" => "DistinctCount",
                _ => ""
            };

            if (normalizedFunction.Length == 0)
                continue;

            var fieldName = (definition.Field ?? "").Trim();
            if (!normalizedFunction.Equals("Count", StringComparison.OrdinalIgnoreCase)
                && (fieldName.Length == 0 || !configuredFields.Contains(fieldName)))
                continue;

            if (fieldName.Length > 0 && !configuredFields.Contains(fieldName))
                continue;

            var baseName = !string.IsNullOrWhiteSpace(definition.Alias)
                ? definition.Alias.Trim()
                : !string.IsNullOrWhiteSpace(definition.Label)
                    ? definition.Label.Trim()
                    : fieldName.Length == 0
                        ? normalizedFunction
                        : $"{normalizedFunction}_{fieldName}";

            if (baseName.Length > 120)
                baseName = baseName[..120];

            var outputName = baseName;
            var suffix = 2;
            while (!names.Add(outputName))
                outputName = $"{baseName}_{suffix++}";

            result.Add(new ResolvedTableAggregate(
                fieldName,
                normalizedFunction,
                outputName,
                string.IsNullOrWhiteSpace(definition.Label)
                    ? outputName
                    : definition.Label.Trim(),
                definition.Format ?? "",
                definition.Unit ?? ""));
        }

        return result;
    }

    private static string AggregateSelectSql(ResolvedTableAggregate aggregate)
    {
        var output = QuoteIdentifier(aggregate.OutputName);

        if (aggregate.Function.Equals("Count", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(aggregate.Field)
                ? $"COUNT_BIG(1) AS {output}"
                : $"COUNT_BIG(src.{QuoteIdentifier(aggregate.Field)}) AS {output}";
        }

        var field = $"src.{QuoteIdentifier(aggregate.Field)}";

        return aggregate.Function switch
        {
            "DistinctCount" => $"COUNT_BIG(DISTINCT {field}) AS {output}",
            "Avg" => $"AVG(TRY_CONVERT(decimal(38,6), {field})) AS {output}",
            "Min" => $"MIN({field}) AS {output}",
            "Max" => $"MAX({field}) AS {output}",
            _ => $"SUM(TRY_CONVERT(decimal(38,6), {field})) AS {output}"
        };
    }

    private static PivotShape ShapePivotRows(
        IReadOnlyList<Dictionary<string, object?>> sourceRows,
        IReadOnlyList<string> rowFields,
        IReadOnlyList<ResolvedTableAggregate> measures,
        int maxColumns,
        int maxRows)
    {
        var pivotValues = sourceRows
            .Select(row => Convert.ToString(
                QueryResult.Get(row, "__dyn_pivot_column"),
                CultureInfo.GetCultureInfo("de-AT")) ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(1, maxColumns))
            .ToArray();

        var groups = sourceRows
            .GroupBy(
                row => string.Join(
                    "\u001f",
                    rowFields.Select(field =>
                        Convert.ToString(
                            QueryResult.Get(row, field),
                            CultureInfo.InvariantCulture) ?? "")),
                StringComparer.Ordinal)
            .Take(Math.Max(1, maxRows))
            .ToArray();

        var columns = new List<string>(rowFields);

        foreach (var pivotValue in pivotValues)
        {
            foreach (var measure in measures)
                columns.Add($"{pivotValue} · {measure.Label}");
        }

        var rows = new List<Dictionary<string, object?>>(groups.Length);

        foreach (var group in groups)
        {
            var first = group.First();
            var shaped = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var field in rowFields)
                shaped[field] = QueryResult.Get(first, field);

            foreach (var pivotValue in pivotValues)
            {
                var cell = group.FirstOrDefault(row =>
                    string.Equals(
                        Convert.ToString(
                            QueryResult.Get(row, "__dyn_pivot_column"),
                            CultureInfo.GetCultureInfo("de-AT")) ?? "",
                        pivotValue,
                        StringComparison.CurrentCultureIgnoreCase));

                foreach (var measure in measures)
                {
                    shaped[$"{pivotValue} · {measure.Label}"] =
                        cell is null ? null : QueryResult.Get(cell, measure.OutputName);
                }
            }

            rows.Add(shaped);
        }

        return new PivotShape(columns, rows);
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
            DynReportInteractiveQueryRules.ShouldUseServerMode(package, component));
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

    private sealed record ResolvedTableAggregate(
        string Field,
        string Function,
        string OutputName,
        string Label,
        string Format,
        string Unit);

    private sealed record PivotShape(
        IReadOnlyList<string> Columns,
        List<Dictionary<string, object?>> Rows);

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
