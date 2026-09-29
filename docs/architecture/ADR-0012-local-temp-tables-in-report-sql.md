# ADR-0012: Local temporary tables in native report SQL

- **Status:** Accepted
- **Date:** 2026-09-29
- **Applies to:** DynReport SQL policy validator and native SQL datasets

## Context

Some proven read-only reporting queries use SQL Server local temporary tables to structure expensive calculations,
build intermediate result sets and add indexes for performance. The Kundencockpit Lager/Kundenartikel calculation is
one such query.

Rejecting every CREATE/DROP/SELECT INTO operation prevents these reports from becoming native even though local
`#temp` objects are connection-scoped and do not persist application data.

## Decision

DynReport permits only these local temporary-object operations in SQL text datasets:

- `SELECT ... INTO #temp`
- `CREATE TABLE #temp`
- `CREATE INDEX ... ON #temp`
- `CREATE CLUSTERED INDEX ... ON #temp`
- `CREATE NONCLUSTERED INDEX ... ON #temp`
- `DROP TABLE #temp`
- optional leading `SET NOCOUNT ON`

Persistent or externally visible DDL remains forbidden.

The validator rejects:

- `SELECT ... INTO dbo.Table` or any non-`#` target
- `CREATE TABLE dbo.Table`
- `CREATE VIEW`, `CREATE PROCEDURE`, etc.
- indexes on persistent tables
- `DROP` against non-`#` objects
- INSERT / UPDATE / DELETE / MERGE / ALTER / TRUNCATE
- EXEC in SQL text datasets
- the other existing forbidden commands and external data-access constructs

Database least-privilege remains the primary security boundary.

## Consequences

- mature SSRS reporting logic can be migrated without turning temporary calculations into application code
- large calculations may keep performance-oriented local temp indexes
- native packages still cannot create or mutate permanent database objects
- temp-table datasets are treated as non-composable client datasets; generic server-side paging/filter wrapping is
  not applied to them
