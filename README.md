# DynReport System

DynReport System is an internal reporting and analytics platform for Windows Server / IIS. It provides a modern migration path from SQL Server Reporting Services while keeping Windows SSO, AD-based permissions and the existing report data/business logic.

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

GitHub Actions builds DynReportSystem-Server-Setup-0.7.8-win-x64.exe.

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
