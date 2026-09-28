# ADR-0008: Server-side aggregate grouping and pivot

- **Status:** Accepted
- **Date:** 2026-09-28
- **Decision owner:** Fuchshofer / DynReport System
- **Applies to:** native table semantics, designer, query runtime and heavy-data renderers

## Context

Server-side paging prevents large detail tables from materializing every raw row in the browser. Analytical reports also need grouped summaries and pivot-style matrices without first transferring all detail rows.

The feature must remain renderer-neutral and must not depend on SQL Server's dynamic PIVOT syntax or a concrete JavaScript grid.

## Decision

Native table components have three semantic presentation modes:

- `Rows` — detail rows
- `Grouped` — grouped dimensions with server-side aggregate measures
- `Pivot` — row dimensions, one pivot column dimension and one or more measures

Supported aggregate functions are:

- Sum
- Average
- Minimum
- Maximum
- Count
- Distinct Count

Only fields declared by the trusted DynReport dataset can participate in grouping, pivot or aggregation.

## Grouped execution

For `PresentationMode=Grouped`, DynReport:

1. applies report parameters and semantic search/filter predicates
2. groups by the configured `GroupBy` fields in SQL Server
3. computes aggregate measures in SQL Server
4. orders and pages the grouped result on the server
5. transfers only the requested aggregate page to the renderer

The renderer receives ordinary semantic columns and does not need to know how SQL grouping was implemented.

## Pivot execution

For `PresentationMode=Pivot`, DynReport does **not** generate vendor-specific dynamic SQL PIVOT statements.

Instead:

1. SQL Server applies filters and report parameters
2. SQL Server groups by the configured row fields plus the pivot column field
3. SQL Server computes all configured measures
4. the query is bounded by maximum pivot columns/rows/cells
5. the small aggregated long-form result is shaped into a matrix by DynReport
6. the renderer displays the resulting dynamic semantic columns

This keeps SQL execution safe and the report model portable while still ensuring raw detail data never needs to reach the browser.

## Automatic server execution

`DataMode=Auto` now has real runtime semantics.

- a table-only composable dataset automatically uses the server query path
- grouped and pivot tables automatically use the server path when the dataset SQL is composable, even if the same dataset also feeds a chart or KPI
- datasets still required by non-table components remain materialized for those components
- explicit `Client` keeps row rendering local and is not valid for grouped/pivot analysis

The designer shows whether the selected dataset SQL is compatible with the server path.

## Bounds

Server pivot output is bounded independently from raw query limits.

Default runtime bounds:

- interactive page size: 1,000
- pivot rows: 250
- pivot aggregate cells: 5,000
- semantic pivot columns: 24 by default, maximum 60

These limits are configurable server-side but report packages cannot disable absolute runtime safety boundaries.

## Renderer independence

The same grouped/pivot contract is usable by:

- the current Blazor server table renderer
- a future MIT-licensed Tabulator adapter
- future exports or analytical renderers

No ECharts, Tabulator, AG Grid or vendor-specific configuration enters the `.dynreport` package.

## Consequences

### Positive

- large analytical tables no longer require raw client-side aggregation
- pivot remains safe and bounded
- customer-cockpit-style analyses can share one dataset between charts and server-side analytical tables
- future grid/rendering changes do not require report migration

### Limitations

- server grouping/pivot requires a composable read-only SELECT
- complex legacy RDL batches using DECLARE, multi-statement SQL or terminal ORDER BY must first be moved behind a database read model/view/TVF or rewritten as a composable query
- expandable hierarchical groups are a later interaction enhancement; 0.12 provides server-aggregated grouped pages and pivot matrices
