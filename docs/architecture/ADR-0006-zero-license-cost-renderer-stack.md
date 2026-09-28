# ADR-0006: Zero-license-cost renderer stack

- **Status:** Accepted
- **Date:** 2026-09-28
- **Decision owner:** Fuchshofer / DynReport System
- **Applies to:** native report viewer, designer and future renderer adapters

## Decision

DynReport uses renderer components that can be deployed and used without recurring license fees or per-developer/per-server runtime license costs.

The approved target stack is:

- **Charts:** Apache ECharts (Apache-2.0)
- **Standard tables:** DynReport's own lightweight Blazor table renderer
- **Heavy-data tables:** Tabulator (MIT) as the preferred future specialized renderer
- **Fallback/reference table capabilities:** ASP.NET Core/Blazor primitives such as QuickGrid may be used where useful
- **Query/data processing:** DynReport Query Runtime + SQL Server

Commercial-only features or components from AG Grid Enterprise, DevExpress, Telerik, Syncfusion or comparable paid libraries are not part of the target architecture.

## Why

Report definitions are long-lived business artifacts. Renderer libraries are replaceable implementation details.

DynReport must therefore avoid:
- license lock-in
- mandatory subscriptions
- per-developer renderer fees
- package definitions tied to one grid/chart vendor
- forcing report migrations when a UI library changes

## Standard vs. heavy-data tables

The runtime may choose a table renderer based on semantic capabilities and data scale.

### Standard renderer

Use the native DynReport table for normal report output:
- column selection/order
- sorting
- filtering
- grouping
- formatting
- paging
- CSV export

### Heavy-data renderer

Use a specialized free renderer such as Tabulator where a report needs:
- large virtualized datasets
- richer grouping
- advanced interactive filtering
- large scrollable detail views
- future remote/server-side paging and filtering

The report package must never name Tabulator. It only declares the required capabilities.

## Designer rule

The designer exposes semantic options only:
- columns
- grouping
- sorting
- filterability
- searchability
- chart type
- category
- measures
- aggregation
- series grouping
- stacking
- labels/legend

The designer must never expose ECharts options, Tabulator configuration objects or vendor-specific APIs.

## Runtime rule

Client-side processing is acceptable only for bounded result sets.

For large datasets, DynReport should progressively move filtering, sorting, grouping, paging and aggregation into the DynReport Query Runtime and SQL Server. The table renderer sends semantic requests; the backend decides how to execute them safely.

## Consequences

- no renderer license budget is required for the planned platform
- ECharts can stay as the current chart adapter
- the current native table remains useful for standard reports
- Tabulator can be introduced later without changing existing `.dynreport` files
- future renderer replacements remain possible under ADR-0005
