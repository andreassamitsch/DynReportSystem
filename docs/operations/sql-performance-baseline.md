# SQL performance and Query Store baseline

This runbook defines the database-side performance baseline for DynReport. It intentionally does not
change Oxaion or Syncos database settings automatically.

## Principle

DynReport application telemetry answers **which report/dataset was slow**. SQL Server Query Store
answers **which query plan and runtime behavior caused it**. Both are required for reliable tuning.

Query Store must be reviewed with the SQL Server/DBA owner before enabling or changing settings on
production Oxaion, Syncos, staging or FU databases.

## Before mass report migration

For every source database used by production DynReports:

1. record SQL Server version/edition and database compatibility level
2. record current Query Store state
3. verify current backup/recovery model and maintenance windows
4. capture a normal workload baseline
5. identify the highest-duration and highest-resource DynReport queries
6. tune the query/view/index before compensating with larger application timeouts
7. document deliberately expensive reports and their expected execution window

## Inspect Query Store state

Run in the target database:

```sql
SELECT
    actual_state_desc,
    desired_state_desc,
    readonly_reason,
    current_storage_size_mb,
    max_storage_size_mb,
    query_capture_mode_desc,
    size_based_cleanup_mode_desc,
    wait_stats_capture_mode_desc
FROM sys.database_query_store_options;
```

Do not enable Query Store from the DynReport installer.

## Find expensive recent queries

When Query Store is enabled, start with an intentionally bounded time window and correlate the SQL
text back to DynReport's report/dataset ID from application telemetry.

```sql
DECLARE @Since datetimeoffset = DATEADD(hour, -24, SYSUTCDATETIME());

SELECT TOP (50)
    q.query_id,
    p.plan_id,
    qt.query_sql_text,
    SUM(rs.count_executions) AS executions,
    SUM(rs.avg_duration * rs.count_executions)
        / NULLIF(SUM(rs.count_executions), 0) AS weighted_avg_duration_us,
    SUM(rs.avg_cpu_time * rs.count_executions)
        / NULLIF(SUM(rs.count_executions), 0) AS weighted_avg_cpu_us,
    SUM(rs.avg_logical_io_reads * rs.count_executions)
        / NULLIF(SUM(rs.count_executions), 0) AS weighted_avg_logical_reads
FROM sys.query_store_query_text qt
JOIN sys.query_store_query q
    ON q.query_text_id = qt.query_text_id
JOIN sys.query_store_plan p
    ON p.query_id = q.query_id
JOIN sys.query_store_runtime_stats rs
    ON rs.plan_id = p.plan_id
JOIN sys.query_store_runtime_stats_interval rsi
    ON rsi.runtime_stats_interval_id = rs.runtime_stats_interval_id
WHERE rsi.end_time >= @Since
GROUP BY
    q.query_id,
    p.plan_id,
    qt.query_sql_text
ORDER BY weighted_avg_duration_us DESC;
```

## DynReport-side thresholds

Current application guardrails are configured below `Runtime`:

- `MaxConcurrentQueries`
- `AbsoluteMaxRows`
- `AbsoluteMaxQuerySeconds`
- `SlowQueryThresholdMs`
- `AbsoluteMaxResultBytes`

Do not raise these globally to hide one badly designed report.

Prefer, in order:

1. narrower report parameters
2. server-side aggregation
3. an approved reporting view/stored procedure
4. appropriate indexing/statistics after DBA review
5. materialized/precomputed reporting tables for genuinely expensive business calculations
6. worker execution for long-running exports/materialization

## Operational warning signs

Investigate when any of these becomes persistent:

- slow-query log rate increases
- query gate saturation causes visible queueing
- result truncation becomes common
- SQL timeouts increase
- the same report repeatedly consumes high CPU/logical reads
- automatic dashboards synchronize into load spikes
- APP-01 memory rises because large datasets are materialized repeatedly

## Change discipline

Any source-database index, Query Store setting, compatibility-level change or plan-forcing action is
an operational database change and is outside the DynReport application installer. Review, test and
document it through the normal DBA process.
