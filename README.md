# DynReport System

DynReport System is an internal reporting and analytics platform for Windows Server / IIS. It provides a modern migration path from SQL Server Reporting Services while keeping Windows SSO, AD-based permissions and the existing report data/business logic.

## Version 0.19.1

Patch release for the production cockpit readability feedback from the first 0.19.0 rollout.

Changes:

- substantially larger text on desktop/TV machine cards
- machine cards use three compact information rows instead of five stacked rows
- desktop cards use wider minimum columns so the important values remain human-readable
- phone layout intentionally uses one full-width card per machine for legibility, while compressing each card to three rows
- order/article and operation share one row; operator/warnings and the two shift KPIs share the next row
- full warning text and secondary metrics remain available after expanding a machine card
- KPI strip and section headings are larger without increasing the mobile card height

No SQL metadata migration is required.

## Version 0.19.0

Version 0.19.0 introduces the compact production machine-board runtime used by the optimized TV production overview.

Changes:

- grouped-board visuals can opt into a compact machine-card mode with one card per machine/work-center row
- machine cards expose the most important production facts first and expand secondary metrics on click
- status colors are reused as a compact card accent instead of painting large areas
- warning-heavy rows show a concise warning count and keep full warning text available in the expanded view
- the production shell removes redundant heading/view-switch chrome and keeps report parameters collapsed for fast shopfloor use
- mobile production dashboards render two compact cards per row on normal phone widths and fall back to one column on very narrow screens
- TV/desktop layouts use a responsive multi-column machine grid
- this release is the minimum runtime for Produktion Übersicht TV package 2.1.x

No SQL metadata migration is required.

## Version 0.13.2

Patch release for the first native Kundencockpit run on upgraded APP-01 installations.

Changes:

- native data source `OxaionPRD` automatically falls back to the proven legacy `Cockpit:ConnectionString` when `DataSources:OxaionPRD:ConnectionString` is still empty
- central `DataSources:OxaionPRD` remains preferred and takes over automatically once configured
- no SQL credentials are written into `.dynreport` packages
- chart and KPI visuals now show the real dataset execution error instead of misleading warnings such as `Visual 'chart' wird nicht dargestellt` or a false zero value
- native Kundencockpit package 1.0.1 removes the duplicated heading and requires runtime 0.13.2

No SQL metadata migration is required.

## Version 0.18.0

Version 0.18.0 focuses on report-runtime performance and makes native detail pages consistently interactive.

Changes:

- native reports execute only datasets required by the active page instead of running the complete package on every parameter change
- page navigation lazy-loads missing datasets and reuses datasets that are still valid
- the per-report dataset cache is parameter-aware: only parameters actually bound by a dataset participate in its cache signature
- changing an unrelated parameter therefore does not invalidate expensive calculations
- independent datasets on one page execute in bounded parallel batches; the default is `Runtime:MaxParallelDatasetsPerReport = 4`
- the existing global query gate remains the outer SQL Server safety limit
- `Bericht ausführen` reuses unaffected datasets; explicit `Aktualisieren` and Auto-Refresh force a fresh execution of the active page
- server-mode tables continue to query only the visible page/filter/sort and are never preloaded with the report page
- native table cells can map columns to report parameters; clicking a mapped customer or salesperson cell applies the global report filter and can re-run the report
- the Designer exposes table cell -> report parameter mappings

The matching Kundencockpit 1.5.0 moves the expensive Lagerwert KPI to a dedicated aggregate dataset. The full Lager/Kundenartikel dataset is executed only when the Lager page is opened. Customer and salesperson cells in the detail tables act as global filters.

ADR-0014 documents page-scoped execution, dependency-aware caching and bounded parallelism.

## Version 0.17.0

Version 0.17.0 adds semantic cockpit interactions and stable customer colors.

Changes:

- multi-page native reports now participate in browser Back/Forward history; leaving a detail page with the browser Back button returns to the previous report page before leaving the report URL
- chart clicks can set report parameters and re-run the complete report
- chart categories / axis labels can act as filters, enabling customer-name drill filtering
- chart series can map to different target pages, e.g. Umsatz -> Umsatz detail and offener Auftragsbestand/Rahmen -> offene AB
- the Designer exposes category/series parameter mapping and series-to-page navigation
- the global `Customer` color set derives a deterministic visualization-safe color from the normalized customer name
- customer colors avoid near-white/neon ranges and remain stable across reports; explicit overrides remain possible
- stacked total labels are now rendered by an independent helper series, so totals also appear when only one stacked component has a value
- compact chart axes no longer force rotated date labels at narrow widths
- compact KPI labels and chart subtitles are more readable, and 12 KPI cards fit in one desktop row on typical widescreen report layouts

The matching Kundencockpit 1.4.0 uses the new interactions for customer filters and series drilldowns and applies stable customer colors to customer-oriented charts.

ADR-0013 documents the semantic interaction and deterministic customer-color model.

## Version 0.16.0

Version 0.16.0 tightens the compact cockpit presentation and adds controlled support for SQL Server local temporary tables.

Changes:

- local SQL Server `#temp` tables are allowed for read-only analytical calculations:
  `SELECT ... INTO #temp`, temp-table indexes and `DROP TABLE #temp`
- persistent DDL and DML remain blocked; `SELECT INTO` to a non-temp target is still rejected
- optional leading `SET NOCOUNT ON` is supported for migrated analytical SQL
- stacked area/line charts now use true semantic stacking, restoring the original Geschäftsbereich area view
- value axes use the configured semantic number format, producing compact de-AT scales such as `2,5 Mio. €`
- stacked total labels are automatically thinned on narrow/multi-month charts to prevent overlap
- visible chart value labels use consistent de-AT/currency formatting

The matching Kundencockpit 1.3.0 fixes the three empty offer-analysis datasets by making empty customer filters NULL-safe, restores the legacy Lager/Kundenartikel calculation with local #temp tables, adds the `Lagerwert kritisch` KPI and a dedicated Lager analysis page, and further optimizes the overview for one-monitor scanning.

ADR-0012 documents the constrained local-temp-table security model.

## Version 0.15.0

Version 0.15.0 adds a dedicated compact analytical cockpit mode for dense desktop dashboards such as the Kundencockpit.

Changes:

- reports can select `Settings.Density = Compact`; the runtime then reduces report chrome, parameter spacing, navigation, dashboard gaps and KPI card height while preserving responsive mobile behavior
- one metric strip can source individual KPIs from different datasets
- KPI cards and overview visuals can define `TargetPageId` and act as navigation into matching detail pages
- native charts support horizontal orientation, per-series chart types, fixed series colors, primary/secondary Y axes, optional area fill and formatted visible labels
- stacked charts can show a formatted total above every stack
- category colors can come from global semantic color sets such as `BusinessArea`, including report-local overrides when required
- Designer support was added for compact density, cross-dataset KPIs, page jumps and the new chart semantics
- native read-only SQL may start with local `DECLARE` statements or a leading semicolon before a CTE; the existing DML/DDL/EXEC/SELECT-INTO restrictions remain active
- ADR-0011 documents compact analytical cockpit architecture

The matching native Kundencockpit 1.2.0 rebuilds the overview around the proven SSRS information hierarchy: a compact KPI strip, a dominant Umsatz/Auftragsbestand/Rahmen chart and two dense rows of supporting sales, offer, order-intake and complaint analytics, with direct jumps into the existing detail pages.

The legacy Lagerwert-kritisch calculation is intentionally not embedded yet because its historical RDL query relies on temporary-table DDL. The native security boundary remains read-only; this calculation should be moved to a dedicated read-only FU view/function/stored procedure before it is added natively.

## Version 0.14.0

Version 0.14.0 restores the richer native Kundencockpit experience and generalizes the required capabilities for all DynReport reports.

Changes:

- multi-page `.dynreport` packages now render as real report pages with a compact page navigation instead of one long continuous document
- native charts add renderer-neutral `WorldMap` and `Treemap` types
- map tooltips can carry semantic detail fields such as the strongest customer for a country
- global named chart color sets are available through `VisualTheme:ColorSets`
- the global `BusinessArea` palette reproduces the original Kundencockpit RDL `GetColor()` mapping
- charts can select a global `ColorSet` and an independent `ColorKeyField`
- individual reports may override only selected keys through `ColorOverrides`
- the Designer exposes global color sets, color-key fields, local overrides, world maps, treemaps and tooltip fields
- legacy Kundencockpit charts also resolve Geschäftsbereich colors through the same global palette
- server table reading now respects `CommandBehavior.SequentialAccess` ordinal order, fixing the interactive table error seen after server-side paging was introduced
- chart `Count` aggregation now counts rows regardless of the measure field's CLR/numeric type
- package version rendering in the report header is corrected
- ADR-0010 documents global visual themes and native multi-page navigation

The matching native Kundencockpit package 1.1.0 uses separate pages for overview, Umsatz/Länder, Bestelleingang, offene AB, Angebote, offene Posten and Reklamationen. Its Bestelleingang/Open-AB datasets reference `[FUCHSHOFER].[FU].[AB_get]` cross-database, while Oxaion data remains in the production catalog.

## Version 0.13.3

Patch release for native Oxaion data-source targeting.

Changes:

- native `OxaionPRD` may reuse server/authentication/TLS settings from the established `Cockpit:ConnectionString`
- the native source now applies its own central target database metadata instead of inheriting the legacy connection's current catalog
- `DataSources:OxaionPRD:Database` defaults to `production`, matching the historic SSRS shared source `/Oxaion PRD`
- in-place upgrades add this non-secret database metadata automatically without overwriting existing production connection strings
- explicitly configured `DataSources:OxaionPRD:ConnectionString` still has precedence
- native visual errors now surface the actual dataset failure instead of the generic “Visual 'chart' wird nicht dargestellt” fallback

No report re-import or SQL metadata migration is required for this patch.

## Version 0.13.1

Patch release for native report onboarding and upgrades from older DynReport installations.

Changes:

- the file import can now create a new native report when the target folder already exists and the current user has effective `Publish` or `Manage` permission there
- replacing an existing report still requires `Publish` or `Manage` on the report
- importing a package that carries its own ACL grants requires `Manage`, preventing ordinary publishers from changing production access
- package imports into unknown folders are rejected instead of silently creating an unauthorized reporting subtree
- in-place upgrades now merge missing bootstrap folders/reports into the preserved `permissions.json` instead of restoring the old catalog unchanged
- existing grants remain untouched during the merge
- current report metadata such as URL, folder and dataset mappings is refreshed from the shipped bootstrap catalog
- the old permissions file is saved with a timestamp before the merged catalog is written
- this fixes upgrades from releases such as 0.8 where the `kundencockpit` ACL/catalog entry could otherwise be missing even though the Razor route existed

No SQL metadata migration is required.

## Version 0.13.0

Version 0.13.0 adds portal-based permission administration for report folders and reports.

Changes:

- a new `/permissions` administration page is linked directly from the reporting portal for users with effective `Manage` rights
- administrators can manage direct rights for folders and reports without editing JSON or rebuilding packages
- folder rights continue to inherit to child folders and reports
- direct and inherited grants are shown separately so inheritance stays visible
- supported principals are Windows users, groups and Windows principals
- supported permissions are `View`, `Run`, `EditLayout`, `Edit`, `Publish` and `Manage`
- saving a target creates an authoritative portal override for its direct grants
- **Quellrechte wiederherstellen** removes the override and immediately restores bootstrap/SSRS/package source grants
- portal overrides are applied after package and migration rights, so later package imports cannot silently overwrite production access decisions
- permission changes are audited through the existing DynReport audit stream when SQL metadata is available
- permission overrides are stored upgrade-safe in `C:\ProgramData\DynReportSystem\Config\permissions-overrides.json`
- writes are serialized, normalized, backed up to `.bak` and replaced atomically
- the installer creates the dedicated ProgramData Config directory and grants the DynReport IIS app pool Modify rights only there
- backup/recovery documentation now explicitly includes the portal permission store
- ADR-0009 documents the permission source/override and inheritance contract

The current permission model remains explicit-allow only. No implicit access or explicit deny rule is introduced.

## Version 0.12.0

Version 0.12.0 adds server-side analytical tables and moves the Kundencockpit onto the generic native runtime whenever its package is installed.

Changes:

- native tables support semantic `PresentationMode=Rows|Grouped|Pivot`
- grouped mode performs configured dimensions + Sum/Avg/Min/Max/Count/DistinctCount in SQL Server
- grouped results are sorted and paged on the server
- pivot mode aggregates in SQL Server and shapes only the bounded aggregate result into a renderer-neutral matrix
- pivot limits are bounded independently through maximum rows, cells and semantic columns
- `DataMode=Auto` now actually selects server execution when safe
- aggregate/pivot tables use server execution automatically when their SQL is composable, even if the same dataset also feeds a chart/KPI
- the designer can configure grouped measures, pivot row fields, pivot column field, measures, column order and limits
- the designer explains whether Auto/Server execution is available for the selected dataset
- aggregate/pivot values retain semantic format/unit metadata in the current server renderer
- the historic `/reports/kundencockpit` URL redirects to the installed native `kundencockpit` package; the old hand-written Razor/RDL page is now only a fallback
- the legacy fallback loads overview datasets with bounded parallelism (default 4) rather than sequentially
- ADR-0008 documents the renderer-neutral server aggregate/pivot contract
- `docs/reports/kundencockpit-modernization-0.12.md` records the Kundencockpit migration baseline and protects later September business logic from being replaced by the older July RDL
- no DynReport metadata SQL schema migration is required

The next interaction enhancement is expandable hierarchical group navigation. The optional MIT Tabulator renderer can now consume the same server query API without changing report definitions.

## Version 0.11.0

Version 0.11.0 adds the first server-side interactive table execution path for large native reports.

Changes:

- native tables can select `DataMode=Server` in the visual designer
- the designer now validates whether the selected dataset SQL is compatible with server mode and shows the result immediately
- table columns can be configured individually with field, header, data type, format, unit, visibility, filterability, sortability and display order
- server-mode tables no longer require the full dataset to be materialized during the normal report run when no other visual consumes that dataset
- a renderer-neutral table query contract carries offset/limit, global search, column filters, sort definitions and report parameters
- SQL Server performs filtering, sorting and `OFFSET/FETCH` paging
- `COUNT_BIG() OVER()` returns the total result count without a separate count query
- configured row-group fields are prepended to the server sort order so group headers remain deterministic across pages
- filter values are parameterized and requested fields are restricted to trusted dataset/table schema fields
- server-mode SQL is intentionally limited to a composable single read-only `SELECT`; complex logic should sit behind a view/table-valued function/read model
- the current Blazor renderer uses the server query service directly
- an authenticated/authorized JSON endpoint exposes the same contract for the planned optional Tabulator heavy-data renderer
- interactive query count, duration and returned-row metrics are recorded separately
- `Runtime:MaxInteractivePageSize` bounds page size (default 1000)
- ADR-0007 documents the server-side interactive query architecture
- no SQL metadata schema migration is required

This is the backend scalability boundary for large tables. A future free Tabulator renderer can now be attached without changing existing `.dynreport` table definitions or duplicating SQL/query behavior.

## Version 0.10.0

Version 0.10.0 establishes the renderer-neutral, zero-license-cost visual contract for native DynReport reports.

Changes:

- native `table` and `chart` components now have explicit semantic configuration models
- table definitions support visible columns, headers, formats, grouping, default sorting, search/filter/sort capabilities and page size
- chart definitions support chart type, category field, series grouping, measures, aggregation, stacking, legend/labels, point limits and height
- the designer edits these semantic properties directly; no ECharts- or grid-vendor-specific settings are stored in `.dynreport`
- the native table renderer consumes the semantic table contract while remaining backward compatible with older packages
- the current ECharts runtime maps semantic chart definitions into ECharts options behind the rendering boundary
- existing native reports without `Table`/`Chart` configuration continue to use the previous automatic behavior
- the report JSON Schema documents the new table/chart contract
- ADR-0005 makes renderer/framework neutrality a binding architecture rule
- ADR-0006 fixes the zero-license-cost renderer strategy: Apache ECharts for charts, the native lightweight table for standard reports, and a future MIT-licensed Tabulator adapter for heavy-data tables
- no SQL schema migration is required

The next heavy-data step is to add server-side paging/filtering/grouping to the DynReport Query Runtime and then attach the optional Tabulator renderer without changing report definitions.

## Version 0.9.0

Version 0.9.0 introduces the first visual, schema-native DynReport designer workbench.

Changes:

- visual page tabs for multi-page reports
- create and remove report pages without leaving the designer
- component palette for headings, text/value blocks, KPI strips, tables, charts and grouped boards
- responsive grid canvas based directly on each page's existing 1-24 column model
- component selection and a dedicated properties inspector
- edit title, subtitle, dataset, desktop/mobile span and CSS class
- move components through the document order, duplicate them or remove them
- simple expression editor for heading/text values using field/count/sum/avg/min/max
- KPI editor for adding/removing metrics and configuring count/field/sum/avg/min/max/distinct-count expressions
- initial grouped-board grouping editor for section and row grouping
- new components receive safe generic defaults from the selected report datasets/fields
- all designer operations edit the same `.dynreport` document model used by the runtime; no report-specific Razor/C# model is introduced
- no SQL schema migration and no package schema migration are required

The grouped-board column/status/rule designer and richer chart/table configuration remain
follow-up work on the same model.

## Version 0.8.0

Version 0.8.0 hardens the SQL revision catalog after the successful APP-01
rollback validation.

Changes:

- existing local transition revisions are migrated once into `dyn.ReportRevision`
  at startup when the metadata database is configured
- historical imports are content-hash deduplicated and never change
  `dyn.Report.ActiveRevisionId`
- the currently installed package is registered first and remains the active
  revision after the historical backfill
- the backfill is idempotent and writes
  `%ProgramData%\DynReportSystem\migration-local-revisions-v1.complete` only
  after all local revision files were processed without errors
- designer saves and package imports now compensate the local package if a
  hard-fail SQL metadata publish fails, preventing SQL/filesystem divergence
- production configuration upgrades merge new non-secret settings without
  overwriting existing values or copying template ConnectionStrings into an
  existing installation
- upgrades from pre-0.8 installations with a real metadata connection set
  `Metadata:FailPublishWhenUnavailable=true` once
- the installer creates a timestamped backup of
  `appsettings.Production.json` before merging settings
- no SQL schema migration is required for 0.8.0

## Version 0.7.9

Version 0.7.9 fixes the confirmed root cause of the DynReport Interactive Server
startup failure on nested routes such as `/designer/{reportId}`.

The browser test on APP-01 proved that `document.baseURI` was the full designer
URL because `App.razor` did not contain a `<base href="/" />` element.

.NET 10 Blazor loads the server JavaScript initializers through the relative URL
`_blazor/initializers`. Without an app base URL, the browser therefore resolved
that request as:

`/designer/_blazor/initializers` -> HTTP 404 with an empty response body

instead of:

`/_blazor/initializers` -> HTTP 200 with `[]`

Blazor then called `response.json()` on the empty 404 body, producing the exact
observed error `SyntaxError: Unexpected end of JSON input`.

Changes:

- add `<base href="/" />` to the document head
- restore normal Interactive Server prerendering for `Routes` and `HeadOutlet`
- remove the temporary 0.7.8 no-prerender startup placeholder
- make client diagnostics resolve relative Blazor URLs against `document.baseURI`
- add a CI guard that fails the build if the required root base element is removed

No IIS, SignalR, SQL metadata, or report-package migration is required for this fix.

## Version 0.7.8

Version 0.7.8 disables prerendering for the top-level Interactive Server
`Routes` and `HeadOutlet`.

APP-01 diagnostics in 0.7.7 showed:

- `Blazor.start()` completed successfully
- no real `/_blazor` fetch or WebSocket request had started yet
- an asynchronous `Unexpected end of JSON input` occurred immediately afterwards

This places the failure before SignalR startup, in the prerender/hydration path.

- `Routes` now uses `InteractiveServerRenderMode(prerender: false)`
- `HeadOutlet` uses the same non-prerendered render mode
- a static startup indicator remains visible until the Interactive Server circuit
  confirms that it is active
- the standalone browser diagnostics remain available even if the circuit fails

No SQL metadata or report-package migration is required.

## Version 0.7.7

Version 0.7.7 adds deep Blazor/SignalR startup tracing after APP-01 showed a
valid manual negotiate response while the framework itself still failed with
`Unexpected end of JSON input`.

- starts Blazor explicitly through the supported `Blazor.start()` path
- captures and displays the `Blazor.start()` promise failure
- injects a SignalR logger into the Blazor circuit startup
- wraps browser `fetch` before Blazor loads and records every `/_blazor`
  request including method, status, content type, body length and a short body preview
- wraps WebSocket creation and records open/error/close events and close codes
- redacts ephemeral connection IDs/tokens from diagnostics before display/copy
- keeps the existing independent health/manual-negotiate probes as a comparison
  against the real framework requests

This release is diagnostic-first: it does not change report logic or SQL metadata.

## Version 0.7.6

Version 0.7.6 changes the IIS hosting boundary for DynReport Interactive Server.

The APP-01 diagnostics showed a successful `/_blazor/negotiate` HTTP 200 response
followed by `Unexpected end of JSON input` inside `blazor.web.js`. This is the
failure shape produced when SignalR receives an empty negotiation body under IIS.

- switches ASP.NET Core hosting from IIS in-process to out-of-process
- Kestrel now runs DynReport behind the ASP.NET Core Module (ANCM)
- IIS remains responsible for HTTPS, Windows Authentication and front-door access
- uses the IIS Integration Windows authentication scheme for the forwarded identity
- the installer safely stops either the legacy in-process `w3wp.exe` worker or
  the new out-of-process `DynReportSystem.exe` worker during upgrades
- `/health/live` reports the hosting process/model for verification
- the client diagnostic now displays negotiate Content-Type, response-body length
  and a short body preview instead of checking HTTP status alone

No DynReport SQL metadata migration is required for this hosting change.

## Version 0.7.5

Version 0.7.5 adds first-class diagnostics for missing Blazor interactivity and
installs the IIS WebSocket prerequisite required by Interactive Server/SignalR.

- a standalone browser diagnostic panel appears when the Interactive Server
  circuit has not become active within seven seconds
- the panel checks `/_framework/blazor.web.js`, `/health/live` and
  `/_blazor/negotiate` and shows the HTTP status values directly in the UI
- diagnosis can be copied without requiring Blazor to be working
- an interactive probe hides the panel as soon as the Blazor circuit is active
- the installer now installs the IIS `Web-WebSockets` feature
- the installer explicitly enables `system.webServer/webSocket` for the
  DynReport IIS site

## Version 0.7.4

Version 0.7.4 fixes Blazor interactivity failures where pages rendered correctly but
buttons, tabs and other `@onclick` actions did not respond.

- removes the custom `autostart=false` / manual `Blazor.start(...)` bootstrap
- restores the framework-supported standard Blazor Web autostart
- isolates PWA service-worker registration from the Blazor startup path
- adds a visible reconnect overlay so a disconnected server circuit is no longer silent

This directly affects the native report designer, import page and all other interactive
server components.

## Version 0.7.3

Version 0.7.3 fixes a native report designer load failure that could show
`Operation is not valid due to the current state of the object.`.

The cause was an optional `DynExpr.Value` represented by an uninitialized
`JsonElement`. Expressions that do not need a constant value (for example
field, aggregate or function expressions) therefore loaded correctly at runtime
but could fail when the designer cloned the document through JSON serialization.

The model now initializes an omitted expression value as JSON `null`, which
preserves the existing expression semantics while making designer cloning and
package re-serialization safe for existing 2.0 packages.

## Version 0.7.2

Version 0.7.2 fixes the SQL metadata bootstrap issue found during the first APP-01 rollout.

- escapes the reserved SQL Server `ROWCOUNT` keyword in the audit table and runtime insert
- changes `dyn.ReportGrant` from an over-wide composite clustered primary key to a bigint
  surrogate clustered key
- preserves logical grant uniqueness with a unique nonclustered index
- adds idempotent migration `sql/002-fix-audit-and-reportgrant-index.sql` for databases where
  the original 0.7.1 schema was already partially executed
- the repair migration records metadata schema version 2

For an affected 0.7.1 database, keep the database and run migration 002, then install 0.7.2.

## Version 0.7.1

Version 0.7.1 closes the first **revision recovery and write-concurrency gaps** before the
SQL catalog becomes the authoritative runtime source.

### 0.7.1 revision safety

- the designer can read immutable revisions from the DynReport metadata database
- only users with `Publish` or `Manage` permission can restore an older SQL revision
- restore requires a two-step confirmation in the designer
- revision bytes are checked against stored package length and SHA-256 before activation
- the restored package is fully validated by the current DynReport runtime before it replaces
  the active local package
- SQL activation updates report metadata, report grants and `ActiveRevisionId` in one transaction
- if SQL activation fails after the local package was replaced, the previous local package is
  restored as a compensating rollback
- native package import, designer save and restore are serialized to prevent overlapping writes
- metadata configuration now correctly falls back to `DataSources:DynReportMetadata` when
  `Metadata:ConnectionString` is empty
- the same SQL TLS validation policy now applies to the DynReport metadata database

Research and operating baseline:

```text
docs/architecture/research-2026-09-27-platform-baseline.md
docs/operations/sql-performance-baseline.md
docs/operations/backup-recovery.md
```

The filesystem package store remains the active runtime source in 0.7.1. The switch to SQL as the
authoritative read path is intentionally deferred until rollback and restore behavior have been
validated on APP-01.

## Version 0.7.0

Version 0.7 establishes the **production architecture foundation** before additional SSRS reports are migrated.

### 0.7.0 architecture foundation

The target architecture is now explicitly documented and partially implemented:

- SQL Server is the long-term authoritative catalog for report metadata, immutable revisions,
  permissions, audit, execution history and background jobs.
- `.dynreport` remains the portable import/export/build artifact rather than the only
  production database.
- current filesystem packages stay supported during the transition so existing installations
  keep running.
- package imports/designer saves can mirror validated immutable revisions into a dedicated
  DynReport metadata database.
- native report SQL passes a defense-in-depth read-only query policy before execution.
- the primary SQL security boundary remains a dedicated least-privilege SQL/Windows identity.
- report packages containing SQL credentials are rejected.
- logical DataSource profiles resolve centrally from server configuration.
- package uploads enforce entry count, expanded-size and compression-ratio limits.
- query execution has global concurrency protection, cancellation, absolute row/time limits,
  structured execution IDs and slow-query logging.
- optional SQL audit/execution tracking is available through the metadata database.
- `/health/live` and `/health/ready` are available for operations monitoring.
- CSP is introduced in report-only mode before enforcing it in production.
- the manifest now supports a minimum runtime version.
- JSON Schema files document the stable package contract.

SQL bootstrap:

```text
sql/001-dynreport-metadata-schema.sql
sql/002-fix-audit-and-reportgrant-index.sql
sql/010-runtime-principal-template.sql
```

Architecture/operations baseline:

```text
docs/architecture/ADR-0001-self-contained-designer-first-dynreport.md
docs/architecture/ADR-0002-central-sql-catalog-and-package-artifacts.md
docs/architecture/ADR-0003-query-security-boundary.md
docs/architecture/ADR-0004-web-runtime-and-designer.md
docs/architecture/research-2026-09-27-platform-baseline.md
docs/operations/backup-recovery.md
docs/operations/sql-performance-baseline.md
docs/roadmap/architecture-foundation.md
schemas/dynreport-manifest-2.0.schema.json
schemas/dynreport-report-2.0.schema.json
```

### RDL policy from 0.7 onward

RDL is **not** a target runtime format.

Existing SSRS/RDL reports are migrated one by one:

1. analyse the original report's business purpose
2. understand SQL/datasets, parameters, calculations, grouping, colors, visibility and actions
3. measure/validate the query
4. intentionally redesign the UX
5. model the result with native generic DynReport components and expressions
6. compare values against the original SSRS report
7. publish a native `.dynreport` revision
8. retire the RDL dependency for that report

The old generic RDL runtime remains only as a temporary compatibility path for reports that
have not yet been manually converted. No further architecture should depend on automatic
RDL-to-DynReport presentation conversion.


Version 0.6 introduces the **self-contained, designer-first report architecture**.

### 0.6.2 portal performance

- package storage no longer enumerates every `.dynreport` file on every report-card/ACL lookup
- ACL catalog uses indexed reports/folders instead of repeated linear searches
- portal snapshots package/import metadata once per circuit instead of doing filesystem/catalog lookups while rendering cards
- hidden-report and hidden-folder ancestry is calculated once rather than repeatedly for every folder counter
- descendant report counts are precomputed in one pass
- View + Run rights are resolved in a single ACL traversal per report
- search no longer performs package-directory scans while typing

### 0.6.1 package import confirmation

- selecting a `.dynreport` file no longer starts an implicit upload
- the selected filename and size are shown before installation
- a clearly visible **Paket importieren** button explicitly starts validation and import
- the button is disabled until a valid `.dynreport` file has been selected
- the import workflow is optimized for mobile browsers as well as desktop

### 0.6.0 self-contained DynReport packages

Converted production reports are no longer presentation overlays on top of RDL files. A finished
`.dynreport` package is the complete runtime definition of the report.

- `.dynreport` is a ZIP-based portable package
- `manifest.json` contains report identity, path, version and access rules
- `report.json` contains data-source references, parameters, calculations, layout and visuals
- `datasets/*.sql` contains the complete dataset SQL/command definitions
- data-source credentials stay centrally configured on APP-01 and are never embedded in packages
- converted reports run through `/reports/package/{reportId}` without reading an RDL
- package import creates a revision before replacing the current version
- installed packages live under `C:\ProgramData\DynReportSystem\Packages`
- revisions live under `C:\ProgramData\DynReportSystem\Revisions`
- the portal treats installed packages as first-class reports and marks them as optimized
- the original SSRS report remains only a migration/validation source for reports that have not yet been converted

### Designer-first contract

The runtime and designer use exactly the same schema. Report-specific C#, Razor or JavaScript is
not allowed for converted reports.

The generic runtime currently supports schema-defined headings/text, metric strips, grouped boards,
tables and charts plus a safe declarative calculation/expression tree. Conditional formatting,
status mappings, hierarchy/grouping and responsive spans are stored in the package.

The initial designer surface can edit metadata, refresh settings, parameters, dataset SQL (with
appropriate permission), component titles/spans and inspect package revisions. Future drag/drop and
richer property editors build on the same document model rather than introducing a second format.

See:

- `docs/architecture/ADR-0001-self-contained-designer-first-dynreport.md`
- `docs/architecture/dynreport-package-schema-2.0.md`

### First converted reference report

`/Reports/TV/Produktion Übersicht TV` is the first report converted to the 2.0 package model.
Its package contains its Syncos dataset SQL, parameters, business calculations, status colors,
permissions, layout and responsive production board. It has no runtime dependency on its original
RDL.


Version 0.3 turns the Kundencockpit pilot into a **generic reporting portal**.

### 0.5.0 manually optimized report packages (superseded prototype)

The 0.5 prototype introduced manual report optimization but still depended on the RDL and a report-specific renderer. This approach is superseded by the 0.6 self-contained package architecture.

- a `.dynreport` targets an existing migrated SSRS report by ID/path
- the original RDL remains the source for SQL, parameters, data sources and permissions
- the file replaces only the presentation/interaction layer
- files can be installed from **Reporting → Berichtsdatei importieren**
- imported files are stored below `C:\ProgramData\DynReportSystem\CustomReports` and survive application updates
- only users with Edit, Publish or Manage permission on the target report may import/replace a definition
- the portal marks installed manual optimizations with an **optimiert** badge
- the first manually analysed report is `/Reports/TV/Produktion Übersicht TV`

#### Produktion Übersicht TV

The original SSRS report was analysed as a live production board rather than a generic table.
The optimized version preserves and exposes its business intent:

- department → machine → order/article → operation hierarchy
- current machine/operation status and SSRS status colors
- active operators and first article inspection state
- setup actual/target with deviation highlighting
- shift cycle actual/target
- current shift quantity actual/target
- total cycle actual/target
- container cycle actual/target and overdue warning
- last container posting time/quantity/person
- unplanned downtime
- compact production KPIs and warning count
- default SSRS parameter logic, with optional exact start/end date-time input
- automatic execution and 60 second TV refresh

### 0.4.1 dynamic-report hotfix

- fixes HTTP 500 on `/reports/dynamic/*` by registering the RDL presentation services in dependency injection
- supports SSRS 2016+ `ReportSections/ReportSection/Body` instead of only legacy top-level `Body`
- enables the RDL-aware layout engine for reports such as **Auslastung Drehen** that use the modern 2016 report schema

### 0.4.0 RDL-aware report presentation

- reads original SSRS report items instead of treating every report as a generic dataset
- preserves original tablix column selection/order and exposes original row grouping as report context
- reads chart category/series expressions, chart type/subtype, stacking, legends and custom/report code colors
- renders SSRS Shape/Doughnut charts, bar/column/line/area charts and gauge panels responsively
- uses original item size/position to derive a modern responsive grid rather than copying the paper canvas pixel-for-pixel
- adds a default **Berichtsansicht** driven by the RDL plus a separate technical **Datasets** view
- keeps the existing optimized Kundencockpit as its specialized dashboard

### 0.3.2 installer hotfix

- waits for the DynReport IIS app pool and its worker process to actually stop before replacing native ANCM files
- force-terminates only a lingering DynReport `w3wp.exe` worker when IIS does not release mapped files in time
- retries locked file replacements for up to 15 seconds
- extracts the update payload before stopping IIS, reducing portal downtime

### 0.3.1 portal startup hotfix

- caches Windows/AD principal checks imported from SSRS instead of repeating thousands of `IsInRole` calls
- resolves the authorized report list once per portal session instead of for every folder counter/render
- prerenders the portal so a delayed Blazor circuit can no longer present only an empty page
- shows a visible catalog error message if imported ACL/catalog loading fails

### Dynamic portal

- hierarchical folder navigation
- Windows SSO and Windows/AD permission inheritance
- searchable report catalog
- hidden SSRS items remain hidden by default and can be shown on demand
- standard reports and linked reports
- responsive desktop/mobile UI
- custom modern reports such as Kundencockpit can coexist with generic migrated reports

### SSRS migration

A private MigrationBundle.zip can be placed next to the server setup. The installer imports it into the application without committing production RDLs or internal SQL metadata to this public repository.

The migration model supports SSRS folders, reports, RDLs, linked reports, shared datasets, shared data sources, Windows principals and item policies. Subscription and schedule definitions are retained as migration metadata.

Stored SSRS data-source passwords are intentionally not extracted because Reporting Services encrypts them. DynReport resolves imported data sources through server-side application configuration and can reuse the credentials from the existing Cockpit SQL connection where appropriate.

### Generic report runtime

Imported RDL reports are parsed at runtime. DynReport extracts and executes their datasets rather than reproducing the old SSRS page layout.

- text SQL and stored procedure datasets
- embedded and shared datasets/data sources
- report query parameter bindings
- String, Integer, Float, Boolean and DateTime parameters
- single-value and multi-value parameters
- static and dataset-driven valid values
- literal, simple date-expression and dataset-driven defaults
- dataset tabs for reports with several result datasets
- automatic bar, line, area and donut visualizations
- searchable/sortable tables with a filter input on every column
- CSV and chart-image export

For legacy, not-yet-converted reports, the imported RDL remains the migration/runtime source. Once a report is converted to a 2.0 `.dynreport` package, the RDL is no longer part of its runtime dependency chain.

## Kundencockpit

The Kundencockpit remains the first hand-optimized DynReport report and demonstrates what migrated reports can become after visual refinement:

- stacked Umsatz / offene AB / Rahmen monthly chart
- totals above each monthly stack
- business-area colors read from the installed RDL
- stacked business-area revenue and order-intake analysis
- compact parameter/filter bar
- KPI cards, rankings, donut charts and drilldowns
- responsive mobile behavior
- per-column table filters and sorting
- world map, OP aging, complaints and inventory visualizations

## Configuration

Production credentials belong only in appsettings.Production.json on APP-01. Never commit them.

Server-side data-source keys:

- DataSources:OxaionPRD:ConnectionString
- DataSources:SyncosPRD:ConnectionString
- DataSources:SyncosPRDHephax:ConnectionString
- DataSources:ReportServerDB:ConnectionString
- DataSources:DynReportMetadata:ConnectionString

For the new metadata database either `Metadata:ConnectionString` or
`DataSources:DynReportMetadata:ConnectionString` can be configured.

The preferred production configuration uses a dedicated Windows service identity / SQL principal
with the minimum required read/execute permissions and SQL TLS certificate validation. Package
connection-string fallback exists only for the migration phase and must not contain credentials.

## Installation / update

GitHub Actions builds DynReportSystem-Server-Setup-0.19.1-win-x64.exe.

For an SSRS migration, keep the setup EXE, MigrationBundle.zip and the optimized Kundencockpit.rdl (when updating it) next to each other. The setup performs an in-place update and preserves the existing production appsettings, local DynReport permissions and IIS/HTTPS bindings.

## Current migration boundaries

The legacy generic RDL runtime remains available only for reports that have not yet been manually
converted. It is no longer the design target.

New native report capabilities are added to the generic DynReport schema/runtime/designer rather
than through report-specific Razor/C# code. Long-running export workers, a fully SQL-authoritative
catalog, distributed caching and the final client-heavy designer are still roadmap items.

Subscription and schedule definitions from SSRS remain migration metadata; native DynReport job
execution is represented in the new metadata schema but the worker process is not yet implemented.

## Security

- Windows Authentication through IIS
- explicit server-side report/folder permission checks
- migrated SSRS Windows-principal permissions
- SQL queries execute only from trusted installed/imported report definitions
- production RDLs and migration bundles are not committed to the public repository
- no report-data caching in the PWA service worker

## Third-party components

- Apache ECharts 6.1.0 — Apache-2.0
- @svg-maps/world 2.0.0 — CC BY 4.0

See THIRD_PARTY_NOTICES.md.

## License

DynReport System itself is proprietary / all rights reserved. See LICENSE.
