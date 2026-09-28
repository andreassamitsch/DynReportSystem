# ADR-0007: Server-side interactive table query runtime

- **Status:** Accepted
- **Date:** 2026-09-28
- **Decision owner:** Fuchshofer / DynReport System
- **Applies to:** native DynReport tables, query runtime, designer and future heavy-data renderers

## Context

Large analytical tables must not require complete datasets to be transferred into the Blazor circuit or browser before sorting, filtering and paging can begin.

The renderer-neutral report model already defines table semantics independently from a concrete grid library. The next scalability boundary is therefore a renderer-neutral server query contract.

## Decision

A native table may set `DataMode=Server`.

In server mode:

1. the full dataset is not materialized during the normal report run when the dataset is consumed only by server-mode tables
2. the table sends a semantic request containing offset, limit, search, column filters and sort definitions
3. DynReport validates all requested fields against the report dataset/table schema
4. filters are parameterized; user text is never concatenated as executable SQL
5. field identifiers are selected only from the trusted schema and SQL-quoted by the runtime
6. DynReport wraps a composable read-only SELECT as a derived source
7. SQL Server performs filtering, ordering and OFFSET/FETCH paging
8. `COUNT_BIG() OVER()` returns the total row count in the same query
9. only the requested page is materialized and returned to the renderer

The same query contract is exposed through an authenticated/authorized JSON endpoint so a later Tabulator renderer can use the identical backend.

## Composable SQL contract

Server-mode datasets intentionally use a strict SQL subset.

The query must:

- use `CommandType=Text`
- start directly with a `SELECT`
- be a single read-only statement
- not contain a terminal/top-level `ORDER BY`
- not rely on `FOR XML`, `FOR JSON` or query `OPTION(...)`

Ordering belongs to the semantic table definition/request in server mode.

Complex source logic should be moved behind a SQL view, table-valued function or another database-side read model and exposed to DynReport through a simple composable SELECT.

This strictness is intentional: DynReport must never attempt unsafe free-form SQL rewriting.

## Table grouping

Row grouping remains a semantic table capability. In server mode the runtime prepends configured group fields to the effective server-side sort order, and the renderer emits group headers for the returned page.

Aggregate/collapsible server grouping and pivoting are separate future capabilities and will extend the same contract rather than introduce a framework-specific API.

## Security

- View + Run authorization is required for the JSON query endpoint
- report/dataset/visual IDs are resolved from installed trusted packages
- requested fields are schema-whitelisted
- values are SQL parameters
- query concurrency uses the existing DynReport execution gate
- absolute query timeout remains enforced
- interactive page size has a server-side maximum
- each query is audited and measured

## Renderer independence

The server query contract contains DynReport concepts only.

The current Blazor table uses it directly. A future Tabulator adapter may call the JSON endpoint. Neither path changes the `.dynreport` table definition.

## Consequences

### Positive

- large tables can start quickly without loading all rows
- browser/Blazor memory use is bounded by page size
- sorting and filtering use SQL Server rather than client memory
- future heavy-data renderers share one secure backend
- no paid grid dependency is introduced

### Cost

- server-mode SQL must be composable
- global contains-search across many columns can still be expensive and should be used selectively
- advanced aggregate grouping/pivot needs a later semantic extension
