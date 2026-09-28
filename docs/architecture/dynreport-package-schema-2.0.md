# DynReport Package Schema 2.0

A `.dynreport` file is a ZIP container and is the complete runtime definition of a converted report.

## Required entries

```text
manifest.json
report.json
datasets/*.sql
```

Optional entries may include documentation and, in later schema revisions, assets/themes/locales.

## manifest.json

Contains portable report identity and access metadata:

- `SchemaVersion`
- `ReportId`
- `Path`
- `FolderPath`
- `Title`
- `Description`
- `Version`
- `CreatedUtc` / `ModifiedUtc`
- `Tags`
- `Grants`

The portal can build report/folder navigation directly from installed packages. A converted report therefore does not need a ReportServer catalog entry to be executable.

## report.json

Contains the designer/runtime model:

- `Settings`
- `DataSources`
- `Datasets`
- `Parameters`
- `Calculations`
- `Pages`

### Data sources

A data source contains a profile/config key and non-secret fallback target metadata.

Example:

```json
{
  "Id": "syncos-prd",
  "Provider": "SQL",
  "ConfigKey": "SyncosPRD",
  "DefaultConnectionString": "server=...;Initial Catalog=..."
}
```

Credentials are resolved from server configuration and are never stored in the package.

### Datasets

A dataset references a SQL file inside the package and maps SQL parameters to report parameters.

```json
{
  "Id": "production",
  "DataSourceId": "syncos-prd",
  "CommandType": "Text",
  "QueryFile": "datasets/production.sql",
  "Parameters": [
    {
      "SqlName": "@Start",
      "ParameterName": "Start",
      "DataType": "DateTime"
    }
  ]
}
```

### Parameters

Parameters are first-class designer objects: type, label, order, default, input mode, static options or dataset-backed options.

### Calculations

Calculations use a safe declarative expression tree. No C#, VB, Razor or JavaScript stored in a package is executed.

Core expression operators in 2.0 include:

- constants / parameters / references
- field / first / last / latest
- sum / average / min / max / count / distinct count / distinct join
- sum distinct
- group count / group sum
- add / subtract / multiply / divide / round
- coalesce / if
- equals / not-equals / comparisons
- and / or / not
- empty / has-value / contains
- now / today / generic time extraction (for example `hour`)

The expression tree is intentionally structured so the visual designer can create and edit it without parsing arbitrary source code.

### Pages and components

A page is a responsive grid. Components are reusable generic runtime/designer building blocks.

Initial component types:

- `heading`
- `text`
- `metric-strip`
- `grouped-board`
- `table`
- `chart`

`grouped-board` is intended for operational/reporting screens that have hierarchical grouping and repeated rows/cards. Its columns can render:

- text
- status with value→color mapping
- state/icon mapping
- actual/target metric with conditional formatting

## Renderer-neutral contract

The `.dynreport` document stores semantic report behavior, not configuration objects from a concrete UI framework.

Charts, tables, filtering, sorting, grouping, aggregation, drilldowns, conditional formatting, selection and responsive behavior are modeled as DynReport concepts. Concrete chart, grid, map or UI libraries are implementation details behind runtime renderer adapters.

Framework-specific option bags such as ECharts options, AG Grid column definitions, Plotly traces or framework component names are not valid extensions of the native report contract.

This permits different renderers to coexist or to be replaced later without migrating existing report definitions. Runtime execution may also choose client-side, server-side or SQL-assisted filtering/sorting/grouping according to dataset size and capability without changing the report semantics.

The binding architecture and enforcement rules are defined in:

- `docs/architecture/ADR-0005-renderer-neutral-report-model.md`

## Interactive table execution

A table may choose its execution mode through the semantic `DataMode` property.

- `Auto` / `Client`: the dataset is materialized by the normal report run and table interaction occurs against the loaded result.
- `Server`: the table uses the DynReport server query contract for page-wise filtering, sorting and paging.

Server mode does not identify a UI framework. The same `.dynreport` definition can be rendered by the current Blazor table or a future heavy-data renderer such as Tabulator.

For `DataMode=Server`, the dataset SQL must be a composable single read-only `SELECT` without its own terminal ordering. The runtime validates requested fields against the trusted dataset/table schema, parameterizes filter values and applies SQL Server `ORDER BY ... OFFSET/FETCH` paging.

Configured row-group fields are prepended to the effective sort order so grouping remains stable across server pages.

See:

- `docs/architecture/ADR-0007-server-side-interactive-table-query.md`

## Designer contract

The runtime renders only schema-defined components. The designer edits only schema-defined components.

Adding a report feature means extending the **generic schema + generic renderer abstraction + generic designer component**, not adding code for a specific report or leaking a third-party framework contract into the package.

## Revision behavior

Importing or saving a package stores the previous package under the DynReport revision store before replacement.

Default server locations:

```text
C:\ProgramData\DynReportSystem\Packages
C:\ProgramData\DynReportSystem\Revisions
```

## Migration lifecycle

```text
SSRS RDL
   │ one-time analysis/migration
   ▼
self-contained .dynreport
   │
   ├─ import/export
   ├─ runtime
   ├─ designer
   └─ revisions
```

Once validated, the RDL is not part of the runtime dependency chain.
