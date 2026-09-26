# DynReport System

DynReport System is an internal reporting and analytics platform for Windows Server / IIS. It provides a modern migration path from SQL Server Reporting Services while keeping Windows SSO, AD-based permissions and the existing report data/business logic.

## Version 0.6.0

Version 0.6 introduces the **self-contained, designer-first report architecture**.

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

Optional imported data-source keys:

- DataSources:OxaionPRD:ConnectionString
- DataSources:SyncosPRD:ConnectionString
- DataSources:SyncosPRDHephax:ConnectionString
- DataSources:ReportServerDB:ConnectionString

If an imported SQL data source has no explicit configuration, DynReport can use the imported server/database target together with credentials from the existing Cockpit:ConnectionString. Whether that succeeds depends on that SQL account's permissions.

## Installation / update

GitHub Actions builds DynReportSystem-Server-Setup-0.6.0-win-x64.exe.

For an SSRS migration, keep the setup EXE, MigrationBundle.zip and the optimized Kundencockpit.rdl (when updating it) next to each other. The setup performs an in-place update and preserves the existing production appsettings, local DynReport permissions and IIS/HTTPS bindings.

## Current migration boundaries

The generic runtime covers common SSRS data and parameter behavior. Complex VB expressions, unusual parameter dependencies, proprietary/custom report items, exact print pagination and subscription delivery can require report-specific refinement.

Subscription and schedule definitions are imported as metadata; DynReport 0.3 does not yet execute SSRS subscriptions.

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
